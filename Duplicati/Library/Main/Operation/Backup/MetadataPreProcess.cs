// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

using System;
using CoCoL;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading;
using Duplicati.Library.Interface;
using Duplicati.Library.Main.Operation.Common;
using Duplicati.Library.Snapshots;

namespace Duplicati.Library.Main.Operation.Backup
{
    /// <summary>
    /// This class processes paths for metadata and emits the metadata blocks for storage.
    /// Folders and symlinks in the database, and paths are forwarded to be scanned for changes
    /// </summary>
    internal static class MetadataPreProcess
    {
        private static readonly string FILELOGTAG = Logging.Log.LogTagFromType(typeof(MetadataPreProcess)) + ".FileEntry";

        public class FileEntry
        {
            // From input
            public string Path;

            // Split
            public long PathPrefixID;
            public string Filename;

            // From database
            public long OldId;
            public DateTime OldModified;
            public long LastFileSize;
            public string OldMetaHash;
            public long OldMetaSize;

            // From filedata
            public DateTime LastWrite;
            public FileAttributes Attributes;

            // After processing metadata
            public IMetahash MetaHashAndSize;
            public bool MetadataChanged;
            public bool TimestampChanged;
        }

        public static Task Run(Channels channels, Snapshots.ISnapshotService snapshot, Options options, BackupDatabase database, long lastfilesetid, ITaskReader taskReader)
        {
            return AutomationExtensions.RunTask(new
            {
                Input = channels.SourcePaths.AsRead(),
                StreamBlockChannel = channels.StreamBlock.AsWrite(),
                Output = channels.ProcessedFiles.AsWrite(),
            },

            async self =>
            {
                var emptymetadata = Utility.WrapMetadata(new Dictionary<string, string>(), options);
                var prevprefix = new KeyValuePair<string, long>(null, -1);

                var CHECKFILETIMEONLY = options.CheckFiletimeOnly;
                var DISABLEFILETIMECHECK = options.DisableFiletimeCheck;

                while (true)
                {
                    var path = await self.Input.ReadAsync();

                    // We ignore the stop signal, but not the pause and terminate
                    await taskReader.ProgressRendevouz().ConfigureAwait(false);

                    var lastwrite = new DateTime(0, DateTimeKind.Utc);
                    var attributes = default(FileAttributes);
                    try
                    {
                        lastwrite = snapshot.GetLastWriteTimeUtc(path);
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.WriteWarningMessage(FILELOGTAG, "TimestampReadFailed", ex, "Failed to read timestamp on \"{0}\"", path);
                    }

                    try
                    {
                        attributes = snapshot.GetAttributes(path);
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.WriteVerboseMessage(FILELOGTAG, "FailedAttributeRead", "Failed to read attributes from {0}: {1}", path, ex.Message);
                    }

                    // If we only have metadata, stop here
                    if (await ProcessMetadata(path, attributes, lastwrite, options, snapshot, emptymetadata, database, self.StreamBlockChannel).ConfigureAwait(false))
                    {
                        try
                        {
                            var split = Database.LocalDatabase.SplitIntoPrefixAndName(path);

                            long prefixid;
                            if (string.Equals(prevprefix.Key, split.Key, StringComparison.Ordinal))
                                prefixid = prevprefix.Value;
                            else
                            {
                                prefixid = await database.GetOrCreatePathPrefix(split.Key);
                                prevprefix = new KeyValuePair<string, long>(split.Key, prefixid);
                            }

                            if (CHECKFILETIMEONLY || DISABLEFILETIMECHECK)
                            {
                                var tmp = await database.GetFileLastModifiedAsync(prefixid, split.Value, lastfilesetid, false);
                                await self.Output.WriteAsync(new FileEntry
                                {
                                    OldId = tmp.Item1,
                                    Path = path,
                                    PathPrefixID = prefixid,
                                    Filename = split.Value,
                                    Attributes = attributes,
                                    LastWrite = lastwrite,
                                    OldModified = tmp.Item2,
                                    LastFileSize = tmp.Item3,
                                    OldMetaHash = null,
                                    OldMetaSize = -1
                                });
                            }
                            else
                            {
                                var res = await database.GetFileEntryAsync(prefixid, split.Value, lastfilesetid);
                                await self.Output.WriteAsync(new FileEntry
                                {
                                    OldId = res == null ? -1 : res.id,
                                    Path = path,
                                    PathPrefixID = prefixid,
                                    Filename = split.Value,
                                    Attributes = attributes,
                                    LastWrite = lastwrite,
                                    OldModified = res == null ? new DateTime(0) : res.modified,
                                    LastFileSize = res == null ? -1 : res.filesize,
                                    OldMetaHash = res == null ? null : res.metahash,
                                    OldMetaSize = res == null ? -1 : res.metasize
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex.IsRetiredException())
                                continue;

                            Logging.Log.WriteWarningMessage(FILELOGTAG, "ProcessingMetadataFailed", ex,
                                "Failed to process entry, path: {0}", path);
                        }
                    }
                }

            });

        }

        /// <summary>
        /// Processes the metadata for the given path.
        /// </summary>
        /// <returns><c>True</c> if the path should be submitted to more analysis, <c>false</c> if there is nothing else to do</returns>
        private static async Task<bool> ProcessMetadata(string path, FileAttributes attributes, DateTime lastwrite, Options options, Snapshots.ISnapshotService snapshot, IMetahash emptymetadata, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel)
        {
            if (snapshot.IsSymlink(path, attributes))
            {
                // Not all reparse points are symlinks.
                // For example, on Windows 10 Fall Creator's Update, the OneDrive folder (and all subfolders)
                // are reparse points, which allows the folder to hook into the OneDrive service and download things on-demand.
                // If we can't find a symlink target for the current path, we won't treat it as a symlink.
                string symlinkTarget = null;
                try
                {
                    symlinkTarget = snapshot.GetSymlinkTarget(path);
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteExplicitMessage(FILELOGTAG, "SymlinkTargetReadFailure", ex, "Failed to read symlink target for path: {0}", path);
                }

                if (!string.IsNullOrWhiteSpace(symlinkTarget))
                {
                    if (options.SymlinkPolicy == Options.SymlinkStrategy.Ignore)
                    {
                        Logging.Log.WriteVerboseMessage(FILELOGTAG, "IgnoreSymlink", "Ignoring symlink {0}", path);
                        return false;
                    }

                    if (options.SymlinkPolicy == Options.SymlinkStrategy.Store)
                    {
                        var metadata = MetadataGenerator.GenerateMetadata(path, attributes, options, snapshot);

                        if (!metadata.ContainsKey("CoreSymlinkTarget"))
                            metadata["CoreSymlinkTarget"] = symlinkTarget;

                        var metahash = Utility.WrapMetadata(metadata, options);
                        await AddSymlinkToOutputAsync(path, DateTime.UtcNow, metahash, database, streamblockchannel).ConfigureAwait(false);

                        Logging.Log.WriteVerboseMessage(FILELOGTAG, "StoreSymlink", "Stored symlink {0}", path);
                        // Don't process further
                        return false;
                    }
                }
                else
                {
                    Logging.Log.WriteVerboseMessage(FILELOGTAG, "FollowingEmptySymlink", "Treating empty symlink as regular path {0}", path);
                }
            }

            if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
            {
                IMetahash metahash;

                if (!options.SkipMetadata)
                {
                    metahash = Utility.WrapMetadata(MetadataGenerator.GenerateMetadata(path, attributes, options, snapshot), options);
                }
                else
                {
                    metahash = emptymetadata;
                }

                Logging.Log.WriteVerboseMessage(FILELOGTAG, "AddDirectory", "Adding directory {0}", path);
                await AddFolderToOutputAsync(path, lastwrite, metahash, database, streamblockchannel).ConfigureAwait(false);
                return false;
            }

            // Regular file, keep going
            return true;
        }

        /// <summary>
        /// Adds metadata to output, and returns the metadataset ID
        /// </summary>
        /// <returns>The metadataset ID.</returns>
        /// <param name="path">The path for which metadata is processed.</param>
        /// <param name="meta">The metadata entry.</param>
        /// <param name="database">The database connection.</param>
        /// <param name="streamblockchannel">The channel to write streams to.</param>
        internal static async Task<Tuple<bool, long>> AddMetadataToOutputAsync(string path, IMetahash meta, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel)
        {
            StreamProcessResult res;
            using (var ms = new MemoryStream(meta.Blob))
                res = await StreamBlock.ProcessStream(streamblockchannel, path, ms, true, CompressionHint.Default);

            return await database.AddMetadatasetAsync(res.Streamhash, res.Streamlength, res.Blocksetid);
        }

        /// <summary>
        /// Adds a file to the output,
        /// </summary>
        /// <param name="filename">The name of the file to record</param>
        /// <param name="lastModified">The value of the lastModified timestamp</param>
        private static async Task AddFolderToOutputAsync(string filename, DateTime lastModified, IMetahash meta, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel)
        {
            var metadataid = await AddMetadataToOutputAsync(filename, meta, database, streamblockchannel).ConfigureAwait(false);
            await database.AddDirectoryEntryAsync(filename, metadataid.Item2, lastModified);
        }

        /// <summary>
        /// Adds a file to the output,
        /// </summary>
        /// <param name="filename">The name of the file to record</param>
        /// <param name="lastModified">The value of the lastModified timestamp</param>
        /// <param name="database">The database to use</param>
        /// <param name="streamblockchannel">The channel to write blocks to</param>
        /// <param name="meta">The metadata ti record</param>
        private static async Task AddSymlinkToOutputAsync(string filename, DateTime lastModified, IMetahash meta, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel)
        {
            var metadataid = await AddMetadataToOutputAsync(filename, meta, database, streamblockchannel).ConfigureAwait(false);
            await database.AddSymlinkEntryAsync(filename, metadataid.Item2, lastModified);
        }

    }
}

