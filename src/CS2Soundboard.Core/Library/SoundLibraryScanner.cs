using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CS2Soundboard.Core.Audio;

namespace CS2Soundboard.Core.Library;

public sealed record SoundFileEntry(string FullPath, string RelativePath, string Name, long Size, DateTime ModifiedUtc);

public sealed record ScanResult(string Directory, IReadOnlyList<SoundFileEntry> Files, string? Error);

/// <summary>Finds supported audio files in a folder (optionally including subfolders).</summary>
public static class SoundLibraryScanner
{
    public static ScanResult Scan(string? directory, bool includeSubfolders)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new ScanResult("", Array.Empty<SoundFileEntry>(), "No sound folder selected.");
        if (!Directory.Exists(directory))
            return new ScanResult(directory, Array.Empty<SoundFileEntry>(), $"The sound folder does not exist or is not accessible: {directory}");

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSubfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            var root = Path.GetFullPath(directory);
            var files = Directory.EnumerateFiles(root, "*", options)
                .Where(AudioDecoder.IsSupportedExtension)
                .Select(p =>
                {
                    var fi = new FileInfo(p);
                    var rel = Path.GetRelativePath(root, p);
                    return new SoundFileEntry(fi.FullName, rel, Path.GetFileNameWithoutExtension(p), fi.Length, fi.LastWriteTimeUtc);
                })
                .OrderBy(e => e.RelativePath, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new ScanResult(root, files, null);
        }
        catch (Exception ex)
        {
            return new ScanResult(directory, Array.Empty<SoundFileEntry>(), $"Could not scan the sound folder: {ex.Message}");
        }
    }
}
