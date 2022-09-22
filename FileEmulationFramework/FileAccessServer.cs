﻿using System.Diagnostics;
using FileEmulationFramework.Lib.Utilities;
using FileEmulationFramework.Utilities;
using Reloaded.Hooks.Definitions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Reloaded.Memory.Pointers;
using static FileEmulationFramework.Lib.Utilities.Native;
using static FileEmulationFramework.Utilities.Native;
using FileEmulationFramework.Interfaces;
using FileEmulationFramework.Lib;
using FileEmulationFramework.Structs;
using Reloaded.Hooks.Definitions.Enums;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FileEmulationFramework;

/// <summary>
/// Class responsible for handling of all file access.
/// </summary>
public static unsafe class FileAccessServer
{
    private static Logger _logger = null!;

    private static readonly Dictionary<IntPtr, FileInformation> _handleToInfoMap = new();
    private static readonly object _threadLock = new();
    private static IHook<NtCreateFileFn> _createFileHook = null!;
    private static IHook<NtReadFileFn> _readFileHook = null!;
    private static IHook<NtSetInformationFileFn> _setFilePointerHook = null!;
    private static IHook<NtQueryInformationFileFn> _getFileSizeHook = null!;
    private static IAsmHook _closeHandleHook = null!;

    private static EmulationFramework _emulationFramework;
    private static Route _currentRoute;

    // Extended to 64-bit, to use with NtReadFile
    private const long FILE_USE_FILE_POINTER_POSITION = unchecked((long)0xfffffffffffffffe);

    /// <summary>
    /// Initialises this instance.
    /// </summary>
    public static void Init(Logger logger, NativeFunctions functions, IReloadedHooks? hooks, EmulationFramework emulationFramework)
    {
        _logger = logger;
        _emulationFramework = emulationFramework;
        _createFileHook = functions.NtCreateFile.Hook(typeof(FileAccessServer), nameof(NtCreateFileImpl)).Activate();
        _readFileHook = functions.NtReadFile.Hook(typeof(FileAccessServer), nameof(NtReadFileImpl)).Activate();
        _setFilePointerHook = functions.SetFilePointer.Hook(typeof(FileAccessServer), nameof(SetInformationFileHook)).Activate();
        _getFileSizeHook = functions.GetFileSize.Hook(typeof(FileAccessServer), nameof(QueryInformationFileImpl)).Activate();
        
        // We need to cook some assembly for NtClose, because Native->Managed
        // transition can invoke thread setup code which will call CloseHandle again
        // and that will lead to infinite recursion
        var utilities = hooks.Utilities;
        var getFileTypeAddr = functions.GetFileType.Address;
        var closeHandleCallbackAddr = (long)utilities.GetFunctionPointer(typeof(FileAccessServer), nameof(CloseHandleCallback));
        
        if (IntPtr.Size == 4)
        {
            _closeHandleHook = hooks.CreateAsmHook(new[]
            {
                "use32",
                
                // we put called address in EDI
                "push edi", // backup EDI (it's non-volatile)
                "push dword [esp+8]", // push handle
                
                // call the function
                $"mov edi, {getFileTypeAddr}", 
                $"call edi",
                
                // Check result
                $"cmp eax, 1",
                $"jne exit",
                    
                    // It's a file, let's fire our callback.
                    "push dword [esp+8]", // push handle
                    $"mov edi, {closeHandleCallbackAddr}",
                    $"call edi",

                // It is a file, call our callback.
                $"exit:",
                $"pop edi",
            }, functions.CloseHandle.Address, AsmHookBehaviour.ExecuteFirst);
        }
        else
        {
            _closeHandleHook = hooks.CreateAsmHook(new[]
            {
                "use64",

                // handle in (RCX)
                $"push rdi", // Backup RDI as it's non-volatile.
                $"mov rdi, rcx", // Backup handle for later.

                // We must allocate 'shadow space' for the functions we will call in the future under MSFT x64 ABI
                $"sub rsp, 32",

                // Call function to determine if file.
                $"mov rax, {getFileTypeAddr}",
                $"call rax",

                // Check result, 
                $"cmp eax, 1",
                $"jne exit",

                    // It's a file, let's fire our callback.
                    $"mov rcx, rdi", // pass first parameter.
                    $"mov rax, {closeHandleCallbackAddr}",
                    $"call rax",
                
                $"exit:",
                $"add rsp, 32",
                $"mov rcx, rdi", // must restore parameter, to pass to orig function.
                $"pop rdi", // must restore, non-volatile
            }, functions.CloseHandle.Address, AsmHookBehaviour.ExecuteFirst);
        }
        
        _closeHandleHook.Activate();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void CloseHandleCallback(IntPtr hfile)
    {
        if (!_handleToInfoMap.Remove(hfile, out var value)) 
            return;
        
        value.Emulator.CloseHandle(hfile, value);
        _logger.Debug("[FileAccessServer] Closed emulated handle: {0}, File: {1}", hfile, value.FilePath);
    }
    
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInformationFileImpl(IntPtr hfile, IO_STATUS_BLOCK* ioStatusBlock, byte* fileInformation, uint length, FileInformationClass fileInformationClass)
    {
        lock (_threadLock)
        {
            var result = _getFileSizeHook.OriginalFunction.Value.Invoke(hfile, ioStatusBlock, fileInformation, length, fileInformationClass);
            if (fileInformationClass != FileInformationClass.FileStandardInformation || !_handleToInfoMap.TryGetValue(hfile, out var info)) 
                return result;

            var information = (FILE_STANDARD_INFORMATION*)fileInformation;
            var oldSize = information->EndOfFile;
            var newSize = info.Emulator.GetFileSize(hfile, info);
            if (newSize != -1)
                information->EndOfFile = newSize;

            _logger.Info("File Size Override | Old: {0}, New: {1} | {2}", oldSize, newSize, info.FilePath);
            return result;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SetInformationFileHook(IntPtr hfile, IO_STATUS_BLOCK* ioStatusBlock, byte* fileInformation, uint length, FileInformationClass fileInformationClass)
    {
        lock (_threadLock)
        {
            return SetInformationFileImpl(hfile, ioStatusBlock, fileInformation, length, fileInformationClass);
        }
    }

    private static int SetInformationFileImpl(IntPtr hfile, IO_STATUS_BLOCK* ioStatusBlock, byte* fileInformation, uint length, FileInformationClass fileInformationClass)
    {
        if (fileInformationClass != FileInformationClass.FilePositionInformation || !_handleToInfoMap.ContainsKey(hfile))
            return _setFilePointerHook.OriginalFunction.Value.Invoke(hfile, ioStatusBlock, fileInformation, length, fileInformationClass);

        var pointer = *(long*)fileInformation;
        _handleToInfoMap[hfile].FileOffset = pointer;
        return _setFilePointerHook.OriginalFunction.Value.Invoke(hfile, ioStatusBlock, fileInformation, length, fileInformationClass);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe int NtReadFileImpl(IntPtr handle, IntPtr hEvent, IntPtr* apcRoutine, IntPtr* apcContext, IO_STATUS_BLOCK* ioStatus, byte* buffer, uint length, long* byteOffset, IntPtr key)
    {
        lock (_threadLock)
        {
            // Check if this is one of our files.
            if (!_handleToInfoMap.TryGetValue(handle, out var info))
                return _readFileHook.OriginalFunction.Value.Invoke(handle, hEvent, apcRoutine, apcContext, ioStatus, buffer, length, byteOffset, key);

            // If it is, prepare to hook it.
            long requestedOffset = byteOffset != (void*)0 ? *byteOffset : FILE_USE_FILE_POINTER_POSITION; // -1 means use current location
            if (requestedOffset == FILE_USE_FILE_POINTER_POSITION)
                requestedOffset = _handleToInfoMap[handle].FileOffset;

            if (_logger.IsEnabled(LogSeverity.Debug))
                _logger.Debug($"[FileAccessServer] Read Request, Buffer: {(long)buffer:X}, Length: {length}, Offset: {requestedOffset}");

            bool result = info.Emulator.ReadData(handle, buffer, length, requestedOffset, info, out var numReadBytes);
            if (result)
            {
                _logger.Debug("[FileAccessServer] Read Success, Length: {0}, Offset: {1}", numReadBytes, requestedOffset);
                requestedOffset += numReadBytes;
                SetInformationFileImpl(handle, ioStatus, (byte*)&requestedOffset, sizeof(long), FileInformationClass.FilePositionInformation);

                // Set number of read bytes.
                ioStatus->Status = 0;
                ioStatus->Information = new(numReadBytes);
                return 0;
            }

            return _readFileHook.OriginalFunction.Value.Invoke(handle, hEvent, apcRoutine, apcContext, ioStatus, buffer, length, byteOffset, key);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int NtCreateFileImpl(IntPtr* handle, FileAccess access, OBJECT_ATTRIBUTES* objectAttributes, IO_STATUS_BLOCK* ioStatus, long* allocSize, uint fileAttributes, FileShare share, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength)
    {
        lock (_threadLock)
        {
            var currentRoute = _currentRoute;
            try
            {
                // Open the handle.
                var ntStatus = _createFileHook.OriginalFunction.Value.Invoke(handle, access, objectAttributes, ioStatus, allocSize, fileAttributes, share, createDisposition, createOptions, eaBuffer, eaLength);

                // We get the file path by asking the OS; as to not clash with redirector.
                var hndl = *handle;
                if (!FilePathResolver.TryGetFinalPathName(hndl, out var newFilePath))
                {
                    _logger.Debug("[FileAccessServer] Can't get final file name.");
                    return ntStatus;
                }

                // Blacklist DLLs to prevent JIT from locking when new assemblies used by this method are loaded.
                // Might want to disable some other extensions in the future; but this is just a temporary bugfix.
                if (newFilePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return ntStatus;

                // Append to route.
                if (_currentRoute.IsEmpty())
                    _currentRoute = new Route(newFilePath);
                else
                    _currentRoute = _currentRoute.Merge(newFilePath);

                _logger.Debug("[FileAccessServer] Accessing: {0}, {1}, Route: {2}", hndl, newFilePath, _currentRoute.FullPath);

                // Try Accept New File
                for (var x = 0; x < _emulationFramework.Emulators.Count; x++)
                {
                    var emulator = _emulationFramework.Emulators[x];
                    if (!emulator.TryCreateFile(hndl, newFilePath, currentRoute.FullPath))
                        continue;

                    _handleToInfoMap[hndl] = new(newFilePath, 0, emulator);
                    return ntStatus;
                }
                
                return ntStatus;
            }
            finally
            {
                _currentRoute = currentRoute;
            }
        }
    }
}