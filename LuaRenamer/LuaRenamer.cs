﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using NLua;
using NLua.Exceptions;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.Attributes;
using Shoko.Plugin.Abstractions.DataModels;

namespace LuaRenamer
{
    [Renamer(RenamerId)]
    // ReSharper disable once ClassNeverInstantiated.Global
    public class LuaRenamer : IRenamer
    {
        private readonly ILogger<LuaRenamer> _logger;
        private const string RenamerId = nameof(LuaRenamer);
        private static readonly Type? Repofact = Utils.GetTypeFromAssemblies("Shoko.Server.Repositories.RepoFactory");
        private static readonly dynamic? VideoLocalRepo = Repofact?.GetProperty("VideoLocal")?.GetValue(null);
        private static readonly dynamic? ImportFolderRepo = Repofact?.GetProperty("ImportFolder")?.GetValue(null);

        private static string _scriptCache = string.Empty;
        private static readonly Dictionary<string, (DateTime setTIme, string filename, IImportFolder destination, string subfolder)> ResultCache = new();

        public MoveEventArgs Args = null!;

        public LuaRenamer(ILogger<LuaRenamer> logger)
        {
            _logger = logger;
        }

        private (string filename, IImportFolder destination, string subfolder)? CheckCache()
        {
            var crc = Args.FileInfo.Hashes.CRC;
            if (Args.Script.Script != _scriptCache)
            {
                _scriptCache = Args.Script.Script;
                ResultCache.Clear();
                return null;
            }
            if (!ResultCache.TryGetValue(crc, out var res))
                return null;
            if (DateTime.UtcNow < res.setTIme + TimeSpan.FromSeconds(2))
                return (res.filename, res.destination, res.subfolder);
            ResultCache.Remove(crc);
            return null;
        }

        public string? GetFilename(RenameEventArgs args)
        {
            Args = new MoveEventArgs
            {
                Cancel = args.Cancel,
                AvailableFolders = new List<IImportFolder>(),
                FileInfo = args.FileInfo,
                AnimeInfo = args.AnimeInfo,
                GroupInfo = args.GroupInfo,
                EpisodeInfo = args.EpisodeInfo,
                Script = args.Script
            };
            try
            {
                CheckBadArgs();
                var result = GetInfo();
                return result?.filename;
            }
            catch (Exception e)
            {
                return $"*Error: {e.Message}";
            }
        }

        public (IImportFolder? destination, string? subfolder) GetDestination(MoveEventArgs args)
        {
            Args = args;
            try
            {
                CheckBadArgs();
                var result = GetInfo();
                return (result?.destination, result?.subfolder);
            }
            catch (Exception e)
            {
                return (null, $"*Error: {e.Message}");
            }
        }

        public (string filename, IImportFolder destination, string subfolder)? GetInfo()
        {
            if (CheckCache() is { } cacheHit)
                return cacheHit;
            Args.AvailableFolders = ((IEnumerable?)ImportFolderRepo?.GetAll())?.Cast<IImportFolder>().ToList() ?? Args.AvailableFolders;
            using var lua = new LuaContext(_logger, Args);
            var env = lua.RunSandboxed();
            var replaceIllegalChars = (bool)env[LuaEnv.replace_illegal_chars];
            var removeIllegalChars = (bool)env[LuaEnv.remove_illegal_chars];
            var useExistingAnimeLocation = (bool)env[LuaEnv.use_existing_anime_location];
            env.TryGetValue(LuaEnv.filename, out var luaFilename);
            env.TryGetValue(LuaEnv.destination, out var luaDestination);
            env.TryGetValue(LuaEnv.subfolder, out var luaSubfolder);

            var filename = luaFilename is string f
                ? (removeIllegalChars ? f : f.ReplacePathSegmentChars(replaceIllegalChars)).CleanPathSegment(true) + Path.GetExtension(Args.FileInfo.Filename)
                : Args.FileInfo.Filename;
            var (destination, subfolder) = (useExistingAnimeLocation ? GetExistingAnimeLocation() : null) ??
                                           (GetNewDestination(luaDestination), GetNewSubfolder(luaSubfolder, replaceIllegalChars, removeIllegalChars));
            if (filename is null || string.IsNullOrWhiteSpace(subfolder)) return null;
            ResultCache.Add(Args.FileInfo.Hashes.CRC, (DateTime.UtcNow, filename, destination, subfolder));
            return (filename, destination, subfolder);
        }

        private string GetNewSubfolder(object? subfolder, bool replaceIllegalChars, bool removeIllegalChars)
        {
            List<string> newSubFolderSplit;
            switch (subfolder)
            {
                case null:
                    newSubFolderSplit = new List<string> { Args.AnimeInfo.First().PreferredTitle };
                    break;
                case LuaTable subfolderTable:
                {
                    var subfolderDict = new SortedDictionary<long, string>();
                    foreach (KeyValuePair<object, object> kvp in subfolderTable)
                    {
                        if (kvp.Key is not long key)
                            continue;
                        if (kvp.Value is not string val)
                            throw new LuaScriptException("subfolder array must only contain strings", string.Empty);
                        subfolderDict[key] = val;
                    }
                    newSubFolderSplit = subfolderDict.Values.ToList();
                    break;
                }
                default:
                    throw new LuaScriptException("subfolder must be an array of path segments or nil", string.Empty);
            }
            newSubFolderSplit = newSubFolderSplit
                .Select(f => (removeIllegalChars ? f : f.ReplacePathSegmentChars(replaceIllegalChars)).CleanPathSegment(false)).ToList();
            var newSubfolder = Path.Combine(newSubFolderSplit.ToArray()).NormPath();
            return newSubfolder;
        }

        private IImportFolder GetNewDestination(object? destination)
        {
            IImportFolder? destfolder;
            switch (destination)
            {
                case null:
                    destfolder = Args.AvailableFolders
                        // Order by common prefix (stronger version of same drive)
                        .OrderByDescending(f => string.Concat(Args.FileInfo.FilePath.NormPath()
                            .TakeWhile((ch, i) => i < f.Location.NormPath().Length
                                                  && char.ToUpperInvariant(f.Location.NormPath()[i]) == char.ToUpperInvariant(ch))).Length)
                        .FirstOrDefault(f => f.DropFolderType.HasFlag(DropFolderType.Destination));
                    if (destfolder is null)
                        throw new ArgumentException("could not find an available destination import folder");
                    break;
                case string str:
                    destfolder = Args.AvailableFolders.FirstOrDefault(f =>
                        string.Equals(f.Name, str, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Location.NormPath(), str.NormPath(), StringComparison.OrdinalIgnoreCase));
                    if (destfolder is null)
                        throw new ArgumentException($"could not find destination folder by name or path: {str}");
                    break;
                case LuaTable destTable:
                    if ((string)destTable[LuaEnv.importfolder._classid] == "55138454-4A0D-45EB-8CCE-1CCF00220165")
                        destfolder = Args.AvailableFolders[Convert.ToInt32(destTable[LuaEnv.importfolder._index])];
                    else
                        throw new ArgumentException($"destination table was not the correct class, assign a table from {LuaEnv.importfolders}");
                    break;
                default:
                    throw new LuaScriptException(
                        $"destination must be nil or an existing import folder string (name/path), or table (see {LuaEnv.importfolders} variable)",
                        string.Empty);
            }
            if (!destfolder.DropFolderType.HasFlag(DropFolderType.Destination))
                throw new ArgumentException($"selected import folder \"{destfolder.Location}\" is not a destination folder, check import folder type");
            return destfolder;
        }

        private (IImportFolder destination, string subfolder)? GetExistingAnimeLocation()
        {
            if (VideoLocalRepo is null || ImportFolderRepo is null) return null;
            IImportFolder? oldFld = null;
            var lastFileLocation = ((IEnumerable<dynamic>)VideoLocalRepo.GetByAniDBAnimeID(Args.AnimeInfo.First().AnimeID))
                .Where(vl => !string.Equals(vl.CRC32, Args.FileInfo.Hashes.CRC, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(vl => vl.DateTimeUpdated)
                .Select(vl => vl.GetBestVideoLocalPlace())
                .FirstOrDefault(vlp => (oldFld = (IImportFolder)ImportFolderRepo.GetByID(vlp.ImportFolderID)) is not null &&
                                       (oldFld.DropFolderType.HasFlag(DropFolderType.Destination) ||
                                        oldFld.DropFolderType.HasFlag(DropFolderType.Excluded)));
            if (oldFld is null || lastFileLocation is null) return null;
            var subFld = Path.GetDirectoryName((string)lastFileLocation.FilePath);
            if (subFld is null) return null;
            return (oldFld, subFld);
        }

        private void CheckBadArgs()
        {
            if (string.IsNullOrWhiteSpace(Args.Script?.Script))
                throw new ArgumentException("Script is empty or null");
            if (Args.Script.Type != RenamerId)
                throw new ArgumentException($"Script doesn't match {RenamerId}");
            if (Args.AnimeInfo.Count == 0 || Args.EpisodeInfo.Count == 0)
                throw new ArgumentException("No anime and/or episode info");
        }
    }
}
