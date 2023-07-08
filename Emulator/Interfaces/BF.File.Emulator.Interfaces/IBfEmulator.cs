﻿using BF.File.Emulator.Interfaces.Structures.IO;

namespace BF.File.Emulator.Interfaces;

public interface IBfEmulator
{
    /// <summary>
    /// Tries to create an emulated BF file by compiling any applicable flow files using the input as the basee
    /// </summary>
    /// <param name="sourcePath">Path to the bf file to use as a base.</param>
    /// <param name="route">The route of the emulated bf file.</param>
    /// <param name="destinationPath">Path to where the emulated bf file should be put.</param>
    public bool TryCreateFromBf(string sourcePath, string route, string destinationPath);

    /// <summary>
    /// Invalidates a file, i.e. unregisters it, will be recreated on next access.
    /// </summary>
    /// <param name="bfPath">Path of the BF file.</param>
    public void InvalidateFile(string bfPath);

    /// <summary>
    /// Gets the list of input files from other mods fed into the emulator.
    /// </summary>
    public RouteFileTuple[] GetEmulatorInput();

    /// <summary>
    /// Registers an already compiled BF as an emulated one
    /// </summary>
    /// <param name="sourcePath">The path to the bf file to registeer</param>
    /// <param name="destinationPath">The path where the emulated bf file should be put</param>
    public void RegisterBf(string sourcePath, string destinationPath);

}
