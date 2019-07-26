﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.CommandLine;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Common;
using Newtonsoft.Json;

namespace ManagedCodeGen
{
    public class jitanalyze
    {
        public class Config
        {
            private ArgumentSyntax _syntaxResult;
            private string _basePath = null;
            private string _diffPath = null;
            private bool _recursive = false;
            private string _fileExtension = ".dasm";
            private bool _full = false;
            private bool _warn = false;
            private int _count = 5;
            private string _json;
            private string _tsv;
            private bool _noreconcile = false;
            private string _note;
            private string _filter;

            public Config(string[] args)
            {
                _syntaxResult = ArgumentSyntax.Parse(args, syntax =>
                {
                    syntax.DefineOption("b|base", ref _basePath, "Base file or directory.");
                    syntax.DefineOption("d|diff", ref _diffPath, "Diff file or directory.");
                    syntax.DefineOption("r|recursive", ref _recursive, "Search directories recursively.");
                    syntax.DefineOption("ext|fileExtension", ref _fileExtension, "File extension to look for.");
                    syntax.DefineOption("c|count", ref _count,
                        "Count of files and methods (at most) to output in the summary."
                      + " (count) improvements and (count) regressions of each will be included."
                      + " (default 5)");
                    syntax.DefineOption("w|warn", ref _warn,
                        "Generate warning output for files/methods that only "
                      + "exists in one dataset or the other (only in base or only in diff).");
                    syntax.DefineOption("note", ref _note,
                        "Descriptive note to add to summary output");
                    syntax.DefineOption("noreconcile", ref _noreconcile,
                        "Do not reconcile unique methods in base/diff");
                    syntax.DefineOption("json", ref _json,
                        "Dump analysis data to specified file in JSON format.");
                    syntax.DefineOption("tsv", ref _tsv,
                        "Dump analysis data to specified file in tab-separated format.");
                    syntax.DefineOption("filter", ref _filter,
                        "Only consider assembly files whose names match the filter");
                });

                // Run validation code on parsed input to ensure we have a sensible scenario.
                validate();
            }

            private void validate()
            {
                if (_basePath == null)
                {
                    _syntaxResult.ReportError("Base path (--base) is required.");
                }

                if (_diffPath == null)
                {
                    _syntaxResult.ReportError("Diff path (--diff) is required.");
                }
            }

            public string BasePath { get { return _basePath; } }
            public string DiffPath { get { return _diffPath; } }
            public bool Recursive { get { return _recursive; } }
            public string FileExtension { get { return _fileExtension; } }
            public bool Full { get { return _full; } }
            public bool Warn { get { return _warn; } }
            public int Count { get { return _count; } }
            public string TSVFileName { get { return _tsv; } }
            public string JsonFileName { get { return _json; } }
            public bool DoGenerateJson { get { return _json != null; } }
            public bool DoGenerateTSV { get { return _tsv != null; } }
            public bool Reconcile { get { return !_noreconcile; } }
            public string Note { get { return _note; } }

            public string Filter {  get { return _filter; } }
        }

        public class FileInfo
        {
            public string path;
            public IEnumerable<MethodInfo> methodList;
        }

        // Custom comparer for the FileInfo class
        private class FileInfoComparer : IEqualityComparer<FileInfo>
        {
            public bool Equals(FileInfo x, FileInfo y)
            {
                if (Object.ReferenceEquals(x, y)) return true;
                if (Object.ReferenceEquals(x, null) || Object.ReferenceEquals(y, null))
                    return false;
                return x.path == y.path;
            }

            // If Equals() returns true for a pair of objects 
            // then GetHashCode() must return the same value for these objects.

            public int GetHashCode(FileInfo fi)
            {
                if (Object.ReferenceEquals(fi, null)) return 0;
                return fi.path == null ? 0 : fi.path.GetHashCode();
            }
        }

        public class MethodInfo
        {
            public string name;
            public int totalBytes;
            public int prologBytes;
            public int functionCount;
            public IEnumerable<int> functionOffsets;
            public override string ToString()
            {
                return String.Format(@"name {0}, total bytes {1}, prolog bytes {2}, "
                    + "function count {3}, offsets {4}",
                    name, totalBytes, prologBytes, functionCount,
                    String.Join(", ", functionOffsets.ToArray()));
            }
        }

        // Custom comparer for the MethodInfo class
        private class MethodInfoComparer : IEqualityComparer<MethodInfo>
        {
            public bool Equals(MethodInfo x, MethodInfo y)
            {
                if (Object.ReferenceEquals(x, y)) return true;
                if (Object.ReferenceEquals(x, null) || Object.ReferenceEquals(y, null))
                    return false;
                return x.name == y.name;
            }

            // If Equals() returns true for a pair of objects 
            // then GetHashCode() must return the same value for these objects.

            public int GetHashCode(MethodInfo mi)
            {
                if (Object.ReferenceEquals(mi, null)) return 0;
                return mi.name == null ? 0 : mi.name.GetHashCode();
            }
        }

        public class FileDelta
        {
            public string basePath;
            public string diffPath;
            public int baseBytes;
            public int diffBytes;
            public int deltaBytes;
            public int reconciledBytesBase;
            public int reconciledCountBase;
            public int reconciledBytesDiff;
            public int reconciledCountDiff;
            public int methodsInBoth;
            public IEnumerable<MethodInfo> methodsOnlyInBase;
            public IEnumerable<MethodInfo> methodsOnlyInDiff;
            public IEnumerable<MethodDelta> methodDeltaList;

            // Adjust lists to include empty methods in diff|base for methods that appear only in base|diff.
            // Also adjust delta to take these methods into account.
            public void Reconcile()
            {
                List<MethodDelta> reconciles = new List<MethodDelta>();

                foreach (MethodInfo m in methodsOnlyInBase)
                {
                    reconciles.Add(new MethodDelta
                    {
                        name = m.name,
                        baseBytes = m.totalBytes,
                        diffBytes = 0,
                        baseOffsets = m.functionOffsets,
                        diffOffsets = null
                    });
                    baseBytes += m.totalBytes;
                    reconciledBytesBase += m.totalBytes;
                    reconciledCountBase++;
                }

                foreach (MethodInfo m in methodsOnlyInDiff)
                {
                    reconciles.Add(new MethodDelta
                    {
                        name = m.name,
                        baseBytes = 0,
                        diffBytes = m.totalBytes,
                        baseOffsets = null,
                        diffOffsets = m.functionOffsets
                    });
                    diffBytes += m.totalBytes;
                    reconciledBytesDiff += m.totalBytes;
                    reconciledCountDiff++;
                }

                methodDeltaList = methodDeltaList.Concat(reconciles);
                deltaBytes = deltaBytes + reconciledBytesDiff - reconciledBytesBase;
            }
        }

        public class MethodDelta
        {
            public string name;
            public int baseBytes;
            public int diffBytes;
            public int deltaBytes { get { return diffBytes - baseBytes; } }
            public IEnumerable<int> baseOffsets;
            public IEnumerable<int> diffOffsets;
        }

        public static IEnumerable<FileInfo> ExtractFileInfo(string path, string filter, bool recursive)
        {
            // if path is a directory, enumerate files and extract
            // otherwise just extract.
            SearchOption searchOption = (recursive) ?
                SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string fullRootPath = Path.GetFullPath(path);

            if (Directory.Exists(fullRootPath))
            {
                string fileNamePattern = filter ?? "*";
                string searchPattern = fileNamePattern + Config.FileExtension;
                return Directory.EnumerateFiles(fullRootPath, searchPattern, searchOption)
                         .Select(p => new FileInfo
                         {
                             path = p.Substring(fullRootPath.Length).TrimStart(Path.DirectorySeparatorChar),
                             methodList = ExtractMethodInfo(p)
                         }).ToList();
            }
            else
            {
                // In the single file case just create a list with a single
                // to satisfy the interface.
                return new List<FileInfo>
                { new FileInfo
                    {
                        path = Path.GetFileName(path),
                        methodList = ExtractMethodInfo(path)
                    }
                };
            }
        }

        // Extract lines of the passed in file and create a method info object
        // for each method descript line containing total bytes, prolog bytes, 
        // and offset in the file.
        public static IEnumerable<MethodInfo> ExtractMethodInfo(string filePath)
        {
            Regex namePattern = new Regex(@"for method (.*)$");
            Regex dataPattern = new Regex(@"code ([0-9]{1,}), prolog size ([0-9]{1,})");
            return File.ReadLines(filePath)
                             .Select((x, i) => new { line = x, index = i })
                             .Where(l => l.line.StartsWith(@"; Total bytes of code", StringComparison.Ordinal)
                                        || l.line.StartsWith(@"; Assembly listing for method", StringComparison.Ordinal))
                             .Select((x) =>
                             {
                                 var nameMatch = namePattern.Match(x.line);
                                 var dataMatch = dataPattern.Match(x.line);
                                 return new
                                 {
                                     name = nameMatch.Groups[1].Value,
                                     // Use matched data or default to 0
                                     totalBytes = dataMatch.Success ?
                                        Int32.Parse(dataMatch.Groups[1].Value) : 0,
                                     prologBytes = dataMatch.Success ?
                                        Int32.Parse(dataMatch.Groups[2].Value) : 0,
                                     // Use function index only from non-data lines (the name line)
                                     functionOffset = dataMatch.Success ?
                                        0 : x.index
                                 };
                             })
                             .GroupBy(x => x.name)
                             .Select(x => new MethodInfo
                             {
                                 name = x.Key,
                                 totalBytes = x.Sum(z => z.totalBytes),
                                 prologBytes = x.Sum(z => z.prologBytes),
                                 functionCount = x.Select(z => z).Where(z => z.totalBytes == 0).Count(),
                                 // for all non-zero function offsets create list.
                                 functionOffsets = x.Select(z => z)
                                                    .Where(z => z.functionOffset != 0)
                                                    .Select(z => z.functionOffset).ToList()
                             }).ToList();
        }

        // Compare base and diff file lists and produce a sorted list of method
        // deltas by file.  Delta is computed diffBytes - baseBytes so positive
        // numbers are regressions. (lower is better)       
        public static IEnumerable<FileDelta> Comparator(IEnumerable<FileInfo> baseInfo,
            IEnumerable<FileInfo> diffInfo, Config config)
        {
            MethodInfoComparer methodInfoComparer = new MethodInfoComparer();
            return baseInfo.Join(diffInfo, b => b.path, d => d.path, (b, d) =>
            {
                var jointList = b.methodList.Join(d.methodList,
                        x => x.name, y => y.name, (x, y) => new MethodDelta
                        {
                            name = x.name,
                            baseBytes = x.totalBytes,
                            diffBytes = y.totalBytes,
                            baseOffsets = x.functionOffsets,
                            diffOffsets = y.functionOffsets
                        })
                        .OrderByDescending(r => r.deltaBytes);

                FileDelta f = new FileDelta
                {
                    basePath = b.path,
                    diffPath = d.path,
                    baseBytes = jointList.Sum(x => x.baseBytes),
                    diffBytes = jointList.Sum(x => x.diffBytes),
                    deltaBytes = jointList.Sum(x => x.deltaBytes),
                    methodsInBoth = jointList.Count(),
                    methodsOnlyInBase = b.methodList.Except(d.methodList, methodInfoComparer),
                    methodsOnlyInDiff = d.methodList.Except(b.methodList, methodInfoComparer),
                    methodDeltaList = jointList.Where(x => x.deltaBytes != 0)
                };

                if (config.Reconcile)
                {
                    f.Reconcile();
                }

                return f;
            }).ToList();
        }

        // Summarize differences across all the files.
        // Output:
        //     Total bytes differences
        //     Top files by difference size
        //     Top diffs by size across all files
        //     Top diffs by percentage size across all files
        //
        public static int Summarize(IEnumerable<FileDelta> fileDeltaList, Config config, Dictionary<string, int> diffCounts)
        {
            var totalDiffBytes = fileDeltaList.Sum(x => x.deltaBytes);
            var totalBaseBytes = fileDeltaList.Sum(x => x.baseBytes);

            if (config.Note != null)
            {
                Console.WriteLine($"\n{config.Note}");
            }

            Console.Write("\nSummary:");
            if (config.Filter != null)
            {
                Console.Write($" (using filter '{config.Filter}')");
            }
            Console.WriteLine("\n(Lower is better)\n");
            Console.WriteLine("Total bytes of diff: {0} ({1:P} of base)", totalDiffBytes, (double)totalDiffBytes / totalBaseBytes);

            if (totalDiffBytes != 0)
            {
                Console.WriteLine("    diff is {0}", totalDiffBytes < 0 ? "an improvement." : "a regression.");
            }

            if (config.Reconcile)
            {
                // See if base or diff had any unique methods
                var uniqueToBase = fileDeltaList.Sum(x => x.reconciledCountBase);
                var uniqueToDiff = fileDeltaList.Sum(x => x.reconciledCountDiff);

                // Only dump reconciliation stats if there was at least one unique
                if (uniqueToBase + uniqueToDiff > 0)
                {
                    var reconciledBytesBase = fileDeltaList.Sum(x => x.reconciledBytesBase);
                    var reconciledBytesDiff = fileDeltaList.Sum(x => x.reconciledBytesDiff);
                    Console.WriteLine("\nTotal byte diff includes {0} bytes from reconciling methods", reconciledBytesDiff - reconciledBytesBase);
                    Console.WriteLine("\tBase had {0,4} unique methods, {1,8} unique bytes", uniqueToBase, reconciledBytesBase);
                    Console.WriteLine("\tDiff had {0,4} unique methods, {1,8} unique bytes", uniqueToDiff, reconciledBytesDiff);
                }
            }

            int requestedCount = config.Count;
            var sortedFileImprovements = fileDeltaList
                                            .Where(x => x.deltaBytes < 0)
                                            .OrderBy(d => d.deltaBytes).ToList();
            var sortedFileRegressions = fileDeltaList
                                            .Where(x => x.deltaBytes > 0)
                                            .OrderByDescending(d => d.deltaBytes).ToList();
            int fileImprovementCount = sortedFileImprovements.Count();
            int fileRegressionCount = sortedFileRegressions.Count();
            int sortedFileCount = fileImprovementCount + fileRegressionCount;
            int unchangedFileCount = fileDeltaList.Count() - sortedFileCount;

            void DisplayFileMetric(string headerText, int metricCount, dynamic list)
            {
                if (metricCount > 0)
                {
                    Console.WriteLine(headerText);
                    foreach (var fileDelta in list.GetRange(0, Math.Min(metricCount, requestedCount)))
                    {
                        Console.WriteLine("    {1,8} : {0} ({2:P} of base)", fileDelta.basePath,
                            fileDelta.deltaBytes, (double)fileDelta.deltaBytes / fileDelta.baseBytes);
                    }
                }
            }

            DisplayFileMetric("\nTop file regressions by size (bytes):", fileRegressionCount, sortedFileRegressions);
            DisplayFileMetric("\nTop file improvements by size (bytes):", fileImprovementCount, sortedFileImprovements);

            Console.WriteLine("\n{0} total files with size differences ({1} improved, {2} regressed), {3} unchanged.",
                sortedFileCount, fileImprovementCount, fileRegressionCount, unchangedFileCount);

            var methodDeltaList = fileDeltaList
                                        .SelectMany(fd => fd.methodDeltaList, (fd, md) => new
                                        {
                                            path = fd.basePath,
                                            name = md.name,
                                            deltaBytes = md.deltaBytes,
                                            baseBytes = md.baseBytes,
                                            diffBytes = md.diffBytes,
                                            baseCount = md.baseOffsets == null ? 0 : md.baseOffsets.Count(),
                                            diffCount = md.diffOffsets == null ? 0 : md.diffOffsets.Count()
                                        }).ToList();
            var sortedMethodImprovements = methodDeltaList
                                            .Where(x => x.deltaBytes < 0)
                                            .OrderBy(d => d.deltaBytes).ToList();
            var sortedMethodRegressions = methodDeltaList
                                            .Where(x => x.deltaBytes > 0)
                                            .OrderByDescending(d => d.deltaBytes).ToList();
            int methodImprovementCount = sortedMethodImprovements.Count();
            int methodRegressionCount = sortedMethodRegressions.Count();
            int sortedMethodCount = methodImprovementCount + methodRegressionCount;
            int unchangedMethodCount = fileDeltaList.Sum(x => x.methodsInBoth) - sortedMethodCount;

            var sortedMethodImprovementsByPercentage = methodDeltaList
                                            .Where(x => x.deltaBytes < 0)
                                            .OrderBy(d => (double)d.deltaBytes / d.baseBytes).ToList();
            var sortedMethodRegressionsByPercentage = methodDeltaList
                                            .Where(x => x.deltaBytes > 0)
                                            .OrderByDescending(d => (double)d.deltaBytes / d.baseBytes).ToList();

            void DisplayMethodMetric(string headerText, int metricCount, dynamic list)
            {
                if (metricCount > 0)
                {
                    Console.WriteLine(headerText);
                    foreach (var method in list.GetRange(0, Math.Min(metricCount, requestedCount)))
                    {
                        Console.Write("    {2,8} ({3,6:P} of base) : {0} - {1}", method.path, method.name, method.deltaBytes,
                            (double)method.deltaBytes / method.baseBytes);

                        if (method.baseCount == method.diffCount)
                        {
                            if (method.baseCount > 1)
                            {
                                Console.Write(" ({0} methods)", method.baseCount);
                            }
                        }
                        else
                        {
                            Console.Write(" ({0} base, {1} diff methods)", method.baseCount, method.diffCount);
                        }
                        Console.WriteLine();
                    }
                }
            }

            DisplayMethodMetric("\nTop method regressions by size (bytes):", methodRegressionCount, sortedMethodRegressions);
            DisplayMethodMetric("\nTop method improvements by size (bytes):", methodImprovementCount, sortedMethodImprovements);
            DisplayMethodMetric("\nTop method regressions by size (percentage):", methodRegressionCount, sortedMethodRegressionsByPercentage);
            DisplayMethodMetric("\nTop method improvements by size (percentage):", methodImprovementCount, sortedMethodImprovementsByPercentage);

            Console.WriteLine("\n{0} total methods with size differences ({1} improved, {2} regressed), {3} unchanged.",
                sortedMethodCount, methodImprovementCount, methodRegressionCount, unchangedMethodCount);

            // Show files with text diffs but not size diffs.
            // TODO: resolve diffs to particular methods in the files.
            var zeroDiffFilesWithDiffs = fileDeltaList.Where(x => diffCounts.ContainsKey(x.diffPath) && (x.deltaBytes == 0))
                .OrderByDescending(x => diffCounts[x.basePath]);

            int zeroDiffFilesWithDiffCount = zeroDiffFilesWithDiffs.Count();
            if (zeroDiffFilesWithDiffCount > 0)
            {
                Console.WriteLine("\n{0} files had text diffs but not size diffs.", zeroDiffFilesWithDiffCount);
                foreach (var zerofile in zeroDiffFilesWithDiffs.Take(config.Count))
                {
                    Console.WriteLine($"{zerofile.basePath} had {diffCounts[zerofile.basePath]} diffs");
                }
            }

            return Math.Abs(totalDiffBytes);
        }

        public static void WarnFiles(IEnumerable<FileInfo> diffList, IEnumerable<FileInfo> baseList)
        {
            FileInfoComparer fileInfoComparer = new FileInfoComparer();
            var onlyInBaseList = baseList.Except(diffList, fileInfoComparer);
            var onlyInDiffList = diffList.Except(baseList, fileInfoComparer);

            //  Go through the files and flag anything not in both lists.

            var onlyInBaseCount = onlyInBaseList.Count();
            if (onlyInBaseCount > 0)
            {
                Console.WriteLine("Warning: {0} files in base but not in diff.", onlyInBaseCount);
                Console.WriteLine("\nOnly in base files:");
                foreach (var file in onlyInBaseList)
                {
                    Console.WriteLine(file.path);
                }
            }

            var onlyInDiffCount = onlyInDiffList.Count();
            if (onlyInDiffCount > 0)
            {
                Console.WriteLine("Warning: {0} files in diff but not in base.", onlyInDiffCount);
                Console.WriteLine("\nOnly in diff files:");
                foreach (var file in onlyInDiffList)
                {
                    Console.WriteLine(file.path);
                }
            }
        }

        public static void WarnMethods(IEnumerable<FileDelta> compareList)
        {
            foreach (var delta in compareList)
            {
                var onlyInBaseCount = delta.methodsOnlyInBase.Count();
                var onlyInDiffCount = delta.methodsOnlyInDiff.Count();

                if ((onlyInBaseCount > 0) || (onlyInDiffCount > 0))
                {
                    Console.WriteLine("Mismatched methods in {0}", delta.basePath);
                    if (onlyInBaseCount > 0)
                    {
                        Console.WriteLine("Base:");
                        foreach (var method in delta.methodsOnlyInBase)
                        {
                            Console.WriteLine("    {0}", method.name);
                        }
                    }
                    if (onlyInDiffCount > 0)
                    {
                        Console.WriteLine("Diff:");
                        foreach (var method in delta.methodsOnlyInDiff)
                        {
                            Console.WriteLine("    {0}", method.name);
                        }
                    }
                }
            }
        }

        public static void GenerateJson(IEnumerable<FileDelta> compareList, string path)
        {
            using (var outputStream = System.IO.File.Create(path))
            {
                using (var outputStreamWriter = new StreamWriter(outputStream))
                {
                    foreach (var file in compareList)
                    {
                        if (file.deltaBytes == 0)
                        {
                            // Early out if there are no diff bytes.
                            continue;
                        }

                        try
                        {
                            // Serialize file delta to output file.
                            outputStreamWriter.Write(JsonConvert.SerializeObject(file, Formatting.Indented));
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Exception serializing JSON: {0}", e.ToString());
                            break;
                        }
                    }
                }
            }
        }

        public static void GenerateTSV(IEnumerable<FileDelta> compareList, string path)
        {
            string schema = "{0}\t{1}\t{2}\t{3}\t{4}";
            using (var outputStream = System.IO.File.Create(path))
            {
                using (var outputStreamWriter = new StreamWriter(outputStream))
                {
                    outputStreamWriter.WriteLine(schema, "File", "Method", "DiffBytes",
                        "BaseBytes", "DeltaBytes");
                    foreach (var file in compareList)
                    {
                        foreach (var method in file.methodDeltaList)
                        {
                            // Method names often contain commas, so use tabs as field separators
                            outputStreamWriter.WriteLine(schema, file.basePath, method.name, method.diffBytes,
                                method.baseBytes, method.deltaBytes);
                        }
                    }
                }
            }
        }

        class ScriptResolverPolicyWrapper : ICommandResolverPolicy
        {
            public CompositeCommandResolver CreateCommandResolver() => ScriptCommandResolverPolicy.Create();
        }

        // There are files with diffs. Build up a dictionary mapping base file name to net text diff count.
        // Use "git diff" to do the analysis for us, then parse that output.
        //
        // "git diff --no-index --exit-code --numstat" output shows added/deleted lines:
        //
        //   <added> <removed> <base-path> => <diff-path>
        //
        // For example:
        // 6       6       "dasmset_8/diff/Vector3Interop_ro/Vector3Interop_ro.dasm" => "dasmset_8/base/Vector3Interop_ro/Vector3Interop_ro.dasm"
        //
        // Note, however, that it can also use a smaller output format, for example:
        //
        // 6       6       dasmset_8/{diff => base}/Vector3Interop_ro/Vector3Interop_ro.dasm
        public static Dictionary<string, int> DiffInText(string diffPath, string basePath)
        {
            // run get diff command to see if we have textual diffs.
            // (use git diff since it's already a dependency and cross platform)
            List<string> commandArgs = new List<string>();
            commandArgs.Add("diff");
            commandArgs.Add("--no-index");
            commandArgs.Add("--exit-code");
            commandArgs.Add("--numstat");
            commandArgs.Add(diffPath);
            commandArgs.Add(basePath);
            Command diffCmd = null;

            try
            {
                diffCmd = Command.Create(new ScriptResolverPolicyWrapper(), @"git", commandArgs);
            }
            catch (CommandUnknownException e)
            {
                Console.Error.WriteLine("\nError: git command not found!  Add git to the environment path.\n", e);
                Environment.Exit(-1);
            }

            diffCmd.CaptureStdOut();
            diffCmd.CaptureStdErr();

            CommandResult result = diffCmd.Execute();
            Dictionary<string, int> fileToTextDiffCount = new Dictionary<string, int>();

            if (result.ExitCode != 0)
            {
                // There are files with diffs. Build up a dictionary mapping base file name to net text diff count.
                //
                // diff --numstat output shows added/deleted lines
                //   added removed base-path => diff-path
                var rawLines = result.StdOut.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                string fullDiffPath = Path.GetFullPath(diffPath);

                foreach (var line in rawLines)
                {
                    string manipulatedLine = line;
                    string[] fields = null;

                    int numFields = 5;

                    // Example output:
                    // 32\t2\t/coreclr/bin/asm/asm/{diff => base}/101301.dasm
                    //
                    // This should be split into:
                    //
                    // 32\t2\t/coreclr/bin/asm/asm/base/101301.dasm\t/coreclr/bin/asm/asm/diff/101301.dasm
                    Regex gitMergedOutputRegex = new Regex(@"\{(\w+)\s=>\s(\w+)\}");
                    Regex whitespaceRegex = new Regex(@"(\w+)\s+(\w+)\s+(.*)");
                    if (gitMergedOutputRegex.Matches(line).Count > 0)
                    {
                        // Do the first split to remove the integers from the file path.
                        // Then reconstruct both the diff and the base paths.
                        var groups = whitespaceRegex.Match(line).Groups;
                        string[] modifiedLine = new string[] {
                            groups[whitespaceRegex.GroupNameFromNumber(1)].ToString(),
                            groups[whitespaceRegex.GroupNameFromNumber(2)].ToString(),
                            groups[whitespaceRegex.GroupNameFromNumber(3)].ToString()
                        };

                        string[] splitLine = gitMergedOutputRegex.Split(modifiedLine[2]);

                        // Split will output:
                        //
                        // 32\t2\t/coreclr/bin/asm/asm/
                        // {diffFolder}
                        // {baseFolder}
                        // 101301.dasm

                        // Create the base path from the second group
                        // {diff => base}
                        string manipulatedBasePath = String.Join(splitLine[2], new string[] {
                            splitLine[0],
                            splitLine[3]
                        });

                        // Create the diff path from the first 
                        // {diff => base}
                        string manipulatedDiffPath = String.Join(splitLine[1], new string[] {
                            splitLine[0],
                            splitLine[3]
                        });

                        fields = new string[4] {
                            modifiedLine[0],
                            modifiedLine[1],
                            manipulatedBasePath,
                            manipulatedDiffPath
                        };

                        numFields = 4;
                    }
                    else
                    {
                        fields = line.Split(new char[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries);
                    }

                    if (fields.Length != numFields)
                    {
                        Console.WriteLine($"Couldn't parse --numstat output '{line}` : {fields.Length} fields");
                        continue;
                    }

                    // store diff-relative path
                    string fullBaseFilePath = Path.GetFullPath(fields[2]);
                    if (!File.Exists(fullBaseFilePath))
                    {
                        Console.WriteLine($"Couldn't parse --numstat output '{line}` : '{fullBaseFilePath}' does not exist");
                        continue;
                    }

                    string baseFilePath = fullBaseFilePath.Substring(fullDiffPath.Length + 1);

                    // Sometimes .dasm is parsed as binary and we don't get numbers, just dashes
                    int addCount = 0;
                    int delCount = 0;
                    Int32.TryParse(fields[0], out addCount);
                    Int32.TryParse(fields[1], out delCount);
                    fileToTextDiffCount[baseFilePath] = addCount + delCount;
                }

                Console.WriteLine($"Found {fileToTextDiffCount.Count()} files with textual diffs.");
            }

            return fileToTextDiffCount;
        }

        public static int Main(string[] args)
        {
            // Parse incoming arguments
            Config config = new Config(args);

            Dictionary<string, int> diffCounts = DiffInText(config.DiffPath, config.BasePath);

            // Early out if no textual diffs found.
            if (diffCounts == null)
            {
                Console.WriteLine("No diffs found.");
                return 0;
            }

            try
            {
                // Extract method info from base and diff directory or file.
                var baseList = ExtractFileInfo(config.BasePath, config.Filter, config.Recursive);
                var diffList = ExtractFileInfo(config.DiffPath, config.Filter, config.Recursive);

                // Compare the method info for each file and generate a list of
                // non-zero deltas.  The lists that include files in one but not
                // the other are used as the comparator function only compares where it 
                // has both sides.

                var compareList = Comparator(baseList, diffList, config);

                // Generate warning lists if requested.
                if (config.Warn)
                {
                    WarnFiles(diffList, baseList);
                    WarnMethods(compareList);
                }

                if (config.DoGenerateTSV)
                {
                    GenerateTSV(compareList, config.TSVFileName);
                }

                if (config.DoGenerateJson)
                {
                    GenerateJson(compareList, config.JsonFileName);
                }

                return Summarize(compareList, config, diffCounts);

            }
            catch (System.IO.DirectoryNotFoundException e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                return 0;
            }
            catch (System.IO.FileNotFoundException e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                return 0;
            }
        }
    }
}
