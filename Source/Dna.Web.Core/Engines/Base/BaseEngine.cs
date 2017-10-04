﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Dna.Web.Core
{
    /// <summary>
    /// A base engine that any specific engine should implement
    /// </summary>
    public abstract class BaseEngine : IDisposable
    {
        #region Protected Members

        /// <summary>
        /// A list of folder watchers that listen out for file changes of the given extensions
        /// </summary>
        protected List<FolderWatcher> mWatchers;

        /// <summary>
        /// The regex to match special tags containing up to 2 values
        /// For example: <!--@ include header @--> to include the file header._dnaweb or header.dnaweb if found
        /// </summary>
        protected string mStandard2GroupRegex = @"<!--@\s*(\w+)\s*(.*?)\s*@-->";

        /// <summary>
        /// The regex to match special tags containing variables and data (which are stored as XML inside the tag)
        /// </summary>
        protected string mStandardVariableRegex = @"<!--\$(.+?(?=\$-->))\$-->";

        /// <summary>
        /// The regex used to find variables to be replaced with the values
        /// </summary>
        protected string mVariableUseRegex = @"\$\$(.+?(?=\$\$))\$\$";

        /// <summary>
        /// The prefixed string in front of a variable to flag it as a special Dna variable
        /// </summary>
        protected string mDnaVariablePrefix = "dna.";

        /// <summary>
        /// The regex used to find a Dna Variable with it's contents wrapped inside Date("contents")
        /// </summary>
        protected string mDnaVariableDateRegex = @"Date\(""(.+?(?=""\)))""\)";

        /// <summary>
        /// The name of the Dna Varaible for getting the executing current directory (project path)
        /// </summary>
        protected string mDnaVariableProjectPath = "ProjectPath";

        /// <summary>
        /// The name of the Dna Varaible for getting the full file path of the file this variable resides inside
        /// </summary>
        protected string mDnaVariableFilePath = "FilePath";

        #endregion

        #region Public Properties

        /// <summary>
        /// The human-readable name of this engine
        /// </summary>
        public abstract string EngineName { get; }

        /// <summary>
        /// The paths to monitor for files
        /// </summary>
        public string MonitorPath { get; set; }

        /// <summary>
        /// The generate files processing option on start up
        /// </summary>
        public GenerateOption GenerateOnStart { get; set; } = GenerateOption.None;

        /// <summary>
        /// The desired default output extension for generated files if not overridden
        /// </summary>
        public string OutputExtension { get; set; } = ".dna";

        /// <summary>
        /// The time in milliseconds to wait for file edits to stop occurring before processing the file
        /// </summary>
        public int ProcessDelay { get; set; } = 300;

        /// <summary>
        /// The filename extensions to monitor for
        /// All files: .*
        /// Specific types: .dnaweb
        /// </summary>
        public List<string> EngineExtensions { get; set; }

        #endregion

        #region Public Events

        /// <summary>
        /// Called when processing of a file succeeded
        /// </summary>
        public event Action<EngineProcessResult> ProcessSuccessful = (result) => { };

        /// <summary>
        /// Called when processing of a file failed
        /// </summary>
        public event Action<EngineProcessResult> ProcessFailed = (result) => { };

        /// <summary>
        /// Called when the engine started
        /// </summary>
        public event Action Started = () => { };

        /// <summary>
        /// Called when the engine stopped
        /// </summary>
        public event Action Stopped = () => { };

        /// <summary>
        /// Called when the engine started watching for a specific file extension
        /// </summary>
        public event Action<string> StartedWatching = (extension) => { };

        /// <summary>
        /// Called when the engine stopped watching for a specific file extension
        /// </summary>
        public event Action<string> StoppedWatching = (extension) => { };

        /// <summary>
        /// Called when a log message is raised
        /// </summary>
        public event Action<LogMessage> LogMessage = (message) => { };

        #endregion

        #region Processing Methods

        /// <summary>
        /// Any pre-processing to do on a file before any other processing is done
        /// </summary>
        /// <param name="data">The file processing information</param>
        /// <returns></returns>
        protected virtual Task PreProcessFile(FileProcessingData data) => Task.FromResult(0);

        /// <summary>
        /// Any post-processing to do on a file after any other processing is done
        /// </summary>
        /// <param name="data">The file processing information</param>
        /// <returns></returns>
        protected virtual Task PostProcessFile(FileProcessingData data) => Task.FromResult(0);

        /// <summary>
        /// Any pre-processing to do before generating the output content
        /// </summary>
        /// <param name="data">The file processing information</param>
        /// <param name="output">The file output information</param>
        /// <returns></returns>
        protected virtual Task PreGenerateFile(FileProcessingData data, FileOutputData output) => Task.FromResult(0);

        /// <summary>
        /// Any post-processing to do after generating the output content
        /// </summary>
        /// <param name="data">The file processing information</param>
        /// <param name="output">The file output information</param>
        /// <returns></returns>
        protected virtual Task PostGenerateFile(FileProcessingData data, FileOutputData output) => Task.FromResult(0);

        /// <summary>
        /// Any post-processing to do after the file has been saved
        /// </summary>
        /// <param name="data">The file processing information</param>
        /// <param name="output">The file output information</param>
        /// <returns></returns>
        protected virtual Task PostSaveFile(FileProcessingData data, FileOutputData output) => Task.FromResult(0);

        /// <summary>
        /// The processing action to perform when the given file has been edited
        /// </summary>
        /// <param name="path">The absolute path of the file to process</param>
        /// <param name="skipFiles">A list of absolute file paths to skip generation on, if any</param>
        /// <returns></returns>
        protected async Task<EngineProcessResult> ProcessFileAsync(string path, List<string> skipFiles = null)
        {
            // Create new processing data
            var processingData = new FileProcessingData { FullPath = path };

            // Keep track of generated files
            var generatedFiles = new List<string>();

            // Make sure the file exists
            if (!FileManager.FileExists(processingData.FullPath))
                return new EngineProcessResult { Success = false, Path = processingData.FullPath, Error = "File no longer exists" };

            // Read all the file into memory (it's ok we will never have large files they are text web files)
            processingData.UnprocessedFileContents = await FileManager.ReadAllText(processingData.FullPath);

            // Pre-processing
            await PreProcessFile(processingData);

            // If it failed
            if (!processingData.Successful)
                // Return the failure
                return new EngineProcessResult { Success = false, Path = path, Error = processingData.Error };

            // Find all outputs
            ProcessOutputTags(processingData);

            // If it failed
            if (!processingData.Successful)
                // Return the failure
                return new EngineProcessResult { Success = false, Path = path, Error = processingData.Error };

            // Process base tags
            ProcessMainTags(processingData);

            // If it failed
            if (!processingData.Successful)
                // Return the failure
                return new EngineProcessResult { Success = false, Path = path, Error = processingData.Error };

            // Process variables and data
            ProcessDataTags(processingData);

            // If it failed
            if (!processingData.Successful)
                // If any failed, return the failure
                return new EngineProcessResult { Success = false, Path = path, Error = processingData.Error };

            // Any post processing
            await PostProcessFile(processingData);

            // If it failed
            if (!processingData.Successful)
                // Return the failure
                return new EngineProcessResult { Success = false, Path = path, Error = processingData.Error };

            // All ok, generate HTML files if not a partial file

            if (!processingData.IsPartial)
            {
                // Generate each output
                processingData.OutputPaths.ForEach(async (outputPath) =>
                {
                    // Skip any files we want to skip
                    if (skipFiles?.Any(toSkip => string.Equals(toSkip, outputPath.FullPath, StringComparison.CurrentCultureIgnoreCase)) == true)
                    {
                        Log($"Skipping already generated file {outputPath.FullPath}", type: LogType.Warning);
                        return;
                    }

                    // Any pre processing
                    await PreGenerateFile(processingData, outputPath);

                    // Compile output (replace variables with values)
                    GenerateOutput(processingData, outputPath);

                    // If we failed, ignore (it will already be logged)
                    if (!processingData.Successful)
                        return;

                    // Any post processing
                    await PostGenerateFile(processingData, outputPath);

                    // Save the contents
                    try
                    {
                        // Save the contents
                        FileManager.SaveFile(outputPath.CompiledContents, outputPath.FullPath);

                        // Any pre processing
                        await PostSaveFile(processingData, outputPath);

                        // Add this to the generated list
                        generatedFiles.Add(outputPath.FullPath);

                        // Log it
                        Log($"Generated file {outputPath.FullPath}");
                    }
                    catch (Exception ex)
                    {
                        // If any failed, return the failure
                        processingData.Error += $"{System.Environment.NewLine}Error saving generated file {outputPath.FullPath}. {ex.Message}. {System.Environment.NewLine}";
                    }
                });
            }
            else
            {
                // If it is a partial file, search the root monitor folder for all files with the extensions
                // and search within those for a tag that includes this partial fie
                // 
                // Then fire off a process event for each of them
                Log($"Partial file edit. Updating referenced files to {path}...");

                // Find all files references this path
                var referencedFiles = await FindReferencedFilesAsync(path);

                // Process any referenced files
                foreach (var reference in referencedFiles)
                {
                    // Process partial
                    var result = await ProcessFileChangedAsync(reference, skipFiles);

                    // Add generated files to list
                    if (result.GeneratedFiles?.Length > 0)
                        generatedFiles.AddRange(result.GeneratedFiles);
                }
            }

            // Return result
            return new EngineProcessResult { Success = processingData.Successful, Path = path, GeneratedFiles = generatedFiles.ToArray(), Error = processingData.Error };
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public BaseEngine()
        {

        }

        #endregion

        #region Engine Methods

        /// <summary>
        /// Starts the engine ready to handle processing of the specified files
        /// </summary>
        public async Task StartAsync()
        {
            Log($"{EngineName} Engine Starting...", type: LogType.Information);
            Log("=======================================", type: LogType.Information);

            // TODO: Add async lock here to prevent multiple calls

            // Dipose of any previous engine setup
            Dispose();

            // Make sure we have extensions
            if (EngineExtensions?.Count == 0)
                throw new InvalidOperationException("No engine extensions specified");

            // Let listener know we started
            Started();

            // Log the message
            Log($"Listening to '{MonitorPath}'...", type: LogType.Information);
            LogTabbed($"Delay", $"{ProcessDelay}ms", 1);

            // Create a new list of watchers
            mWatchers = new List<FolderWatcher>();

            // We need to listen out for file changes per extension
            EngineExtensions.ForEach(extension => mWatchers.Add(new FolderWatcher
            {
                Filter = "*" + extension,
                Path = MonitorPath,
                UpdateDelay = ProcessDelay
            }));

            // Listen on all watchers
            mWatchers.ForEach(watcher =>
            {
                // Listen for file changes
                watcher.FileChanged += Watcher_FileChangedAsync;

                // Inform listener
                StartedWatching(watcher.Filter);

                // Log the message
                LogTabbed($"File Type", watcher.Filter, 1);

                // Start watcher
                watcher.Start();
            });

            // Closing comment tag
            Log("");

            // Now do startup generation
            await StartupGenerationAsync();
        }

        /// <summary>
        /// Performs any startup generation that was specified
        /// </summary>
        private async Task StartupGenerationAsync()
        {
            // If there is nothing to do, just return
            if (GenerateOnStart == GenerateOption.None)
                return;

            // Find all paths
            var allFiles = FindAllFiles();

            // Keep a log of all files that have been generated already
            // so we do not regenerate them
            var generatedFiles = new List<string>();

            // For each file
            foreach (var file in allFiles)
            {
                // Don't process files twice
                if (generatedFiles.Any(f => string.Equals(f, file, StringComparison.CurrentCultureIgnoreCase)))
                    continue;

                // Process file
                var result = await ProcessFileChangedAsync(file, generatedFiles);

                // Add any generated file outputs to list
                if (result.GeneratedFiles?.Length > 0)
                    generatedFiles.AddRange(result.GeneratedFiles);
            };
        }

        #endregion

        #region File Changed

        /// <summary>
        /// Fired when a watcher has detected a file change
        /// </summary>
        /// <param name="path">The path of the file that has changed</param>
        private async void Watcher_FileChangedAsync(string path)
        {
            // Process the file
            await ProcessFileChangedAsync(path);
        }

        /// <summary>
        /// Called when a file has changed and needs processing
        /// </summary>
        /// <param name="path">The full path of the file to process</param>
        /// <param name="skipFiles">A list of absolute file paths to skip generation on, if any</param>
        /// <returns></returns>
        protected async Task<EngineProcessResult> ProcessFileChangedAsync(string path, List<string> skipFiles = null)
        {
            try
            {
                // Log the start
                Log($"Processing file {path}...", type: LogType.Information);

                // Process the file
                var result = await ProcessFileAsync(path, skipFiles);

                // Check if we have an unknown response
                if (result == null)
                    throw new ArgumentNullException("Unknown error processing file. No result provided");

                // If we succeeded, let the listeners know
                if (result.Success)
                {
                    // Inform listeners
                    ProcessSuccessful(result);

                    // Log the message
                    Log($"Successfully processed file {path}", type: LogType.Success);
                }
                // If we failed, let the listeners know
                else
                {
                    // Inform listeners
                    ProcessFailed(result);

                    // Log the message
                    Log($"Failed to processed file {path}", message: result.Error, type: LogType.Error);
                }

                return result;
            }
            // Catch any unexpected failures
            catch (Exception ex)
            {
                // Create result
                var failedResult = new EngineProcessResult
                {
                    Path = path,
                    Error = ex.Message,
                    Success = false,
                };

                // Generate an unexpected error report
                ProcessFailed(failedResult);

                // Log the message
                Log($"Unexpected fail to processed file {path}", message: ex.Message, type: LogType.Error);

                // Return the result
                return failedResult;
            }
        }

        #endregion

        #region Command Tags

        /// <summary>
        /// Processes the tags and finds all output tags
        /// </summary>
        /// <param name="data">The file processing data</param>
        public void ProcessOutputTags(FileProcessingData data)
        {
            // Find all special tags that have 2 groups
            var match = Regex.Match(data.UnprocessedFileContents, mStandard2GroupRegex, RegexOptions.Singleline);

            // No error to start with
            data.Error = string.Empty;

            //
            // NOTE: Only look for the partial tag on the first run as it must be the top of the file
            //       and after that includes could end up adding partials to the parent confusing the situation
            //
            //       So make sure partials are set at the top of the file
            //
            var firstMatch = true;

            // Store original contents
            var tempContents = data.UnprocessedFileContents;

            // Loop through all matches
            while (match.Success)
            {
                // NOTE: The first group is the full match
                //       The second group and onwards are the matches

                // Make sure we have enough groups
                if (match.Groups.Count < 2)
                {
                    data.Error = $"Malformed match {match.Value}";
                    return;
                }

                // Take the first match as the header for the type of tag
                var tagType = match.Groups[1].Value.ToLower().Trim();

                // Now process each tag type
                switch (tagType)
                {
                    // PARTIAL CLASS
                    case "partial":

                        // Only update flag if it is the first match
                        // so includes don't mess it up
                        if (firstMatch)
                            data.IsPartial = true;

                        // Remove tag
                        ReplaceTag(data, match, string.Empty);

                        break;

                    // OUTPUT NAME
                    case "output":

                        // Make sure we have enough groups
                        if (match.Groups.Count < 3)
                        {
                            data.Error = $"Malformed match {match.Value}";
                            return;
                        }

                        // Get output path
                        var outputPath = match.Groups[2].Value;

                        // Process the output command
                        ProcessOutputTag(data, outputPath, match);

                        if (!data.Successful)
                            // Return false if it fails
                            return;

                        break;

                    // UNKNOWN (just ignore)
                    default:
                        ReplaceTag(data, match, string.Empty);
                        break;
                }

                // Find the next command
                match = Regex.Match(data.UnprocessedFileContents, mStandard2GroupRegex, RegexOptions.Singleline);

                // No longer the first match
                firstMatch = false;
            }

            // Restore contents
            data.UnprocessedFileContents = tempContents;

            // If this isn't a partial class, and we have no outputs specified
            // Create a default one
            if (!data.IsPartial && data.OutputPaths.Count == 0)
            {
                // Get default output name
                data.OutputPaths.Add(new FileOutputData
                {
                    FullPath = GetDefaultOutputPath(data.FullPath),
                    FileContents = data.UnprocessedFileContents
                });
            }

            // Now set file contents
            data.OutputPaths.ForEach(output => output.FileContents = data.UnprocessedFileContents);
        }

        /// <summary>
        /// Processes an Output name command to add an output path
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="outputPath">The include path, typically a relative path</param>
        /// <param name="match">The original match that found this information</param>
        /// <returns></returns>
        protected void ProcessOutputTag(FileProcessingData data, string outputPath, Match match)
        {
            // No error to start with
            data.Error = string.Empty;

            // Profile name
            string profileName = null;

            // If the name have a single : then the right half is the profile name
            if (outputPath.Count(c => c == ':') == 1)
            {
                // Set profile path
                profileName = outputPath.Split(':')[1];

                // Set output path
                outputPath = outputPath.Split(':')[0];
            }

            // Get the full path from the provided relative path based on the input files location
            var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(data.FullPath), outputPath));

            // Add this to the list
            data.OutputPaths.Add(new FileOutputData
            {
                FullPath = fullPath,
                ProfileName = profileName,
            });

            // Remove the tag
            ReplaceTag(data, match, string.Empty);
        }

        /// <summary>
        /// Processes the tags in the list and edits the files contents as required
        /// </summary>
        /// <param name="data">The file processing data</param>
        public void ProcessMainTags(FileProcessingData data)
        {
            // For each output
            data.OutputPaths.ForEach(output =>
            {
                #region Find Includes 

                // Find all special tags that have 2 groups
                var match = Regex.Match(output.FileContents, mStandard2GroupRegex, RegexOptions.Singleline);

                // No error to start with
                data.Error = string.Empty;

                // Keep track of all includes to monitor for circular references
                var includes = new List<string>();

                // Loop through all matches
                while (match.Success)
                {
                    // NOTE: The first group is the full match
                    //       The second group and onwards are the matches

                    // Make sure we have enough groups
                    if (match.Groups.Count < 2)
                    {
                        data.Error = $"Malformed match {match.Value}";
                        return;
                    }

                    // Take the first match as the header for the type of tag
                    var tagType = match.Groups[1].Value.ToLower().Trim();

                    // Now process each tag type
                    switch (tagType)
                    {
                        // Remove partial and outputs (already processed)
                        case "partial":
                        case "output":
                            ReplaceTag(output, match, string.Empty);
                            break;

                        case "inline":

                            // Make sure we have enough groups
                            if (match.Groups.Count < 3)
                            {
                                data.Error = $"Malformed match {match.Value}";
                                return;
                            }

                            // Get inline data
                            var inlineData = match.Groups[2].Value;

                            // Process the include command
                            ProcessInlineTag(data, output, inlineData, match);

                            if (!data.Successful)
                                // Return false if it fails
                                return;

                            break;

                        // INCLUDE (Replace file)
                        case "include":

                            // Make sure we have enough groups
                            if (match.Groups.Count < 3)
                            {
                                data.Error = $"Malformed match {match.Value}";
                                return;
                            }

                            // Get include path
                            var includePath = match.Groups[2].Value;

                            // Make sure we have not already included it in this run
                            if (includes.Contains(includePath.ToLower().Trim()))
                            {
                                data.Error = $"Circular reference detected {includePath}";
                                return;
                            }

                            // Process the include command
                            ProcessIncludeTag(data, output, includePath, match);

                            if (!data.Successful)
                                // Return false if it fails
                                return;

                            // Add this to the list of already processed includes
                            includes.Add(includePath.ToLower().Trim());

                            break;

                        // UNKNOWN
                        default:
                            // Report error of unknown match
                            data.Error = $"Unknown match {match.Value}";
                            return;
                    }

                    // Find the next command
                    match = Regex.Match(output.FileContents, mStandard2GroupRegex, RegexOptions.Singleline);
                }

                #endregion
            });
        }

        /// <summary>
        /// Processes an Inline command to replace a tag with the contents of the data between the tags
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="output">The file output data</param>
        /// <param name="inlineData">The inline data</param>
        /// <param name="match">The original match that found this information</param>
        protected void ProcessInlineTag(FileProcessingData data, FileOutputData output, string inlineData, Match match)
        {
            // No error to start with
            data.Error = string.Empty;

            // Profile name
            string profileName = null;

            // If the name starts with : then left half is the profile name
            if (inlineData[0] == ':')
            {
                // Set profile path
                // Find first index of a space or newline
                profileName = inlineData.Substring(1, inlineData.IndexOfAny(new[] { ' ', '\r', '\n' }) - 1);

                // Set inline data (+2 to exclude the space after the profile name and the starting :
                inlineData = inlineData.Substring(profileName.Length + 2);
            }

            // NOTE: A blank profile should be included for everything
            //       A ! means only include if no specific profile name is given
            //       Anything else is the a profile name so should only include if matched

            // If the profile is blank, always include it
            if (string.IsNullOrEmpty(profileName) ||
                // Or if we specify ! only include it if the specified profile is  blank
                (profileName == "!" && string.IsNullOrEmpty(output.ProfileName)) ||
                // Or if the profile name matches include it
                string.Equals(output.ProfileName, profileName, StringComparison.CurrentCultureIgnoreCase))
            {
                // Replace the tag with the contents
                ReplaceTag(output, match, inlineData, removeNewline: false);
            }
            // Remove include tag and finish
            else
            {
                ReplaceTag(output, match, string.Empty);
            }
        }

        /// <summary>
        /// Processes an Include command to replace a tag with the contents of another file
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="output">The file output data</param>
        /// <param name="includePath">The include path, typically a relative path</param>
        /// <param name="match">The original match that found this information</param>
        protected void ProcessIncludeTag(FileProcessingData data, FileOutputData output, string includePath, Match match)
        {
            // No error to start with
            data.Error = string.Empty;

            // Profile name
            string profileName = null;

            // If the name have a single : then the right half is the profile name
            if (includePath.Count(c => c == ':') == 1)
            {
                // Set profile path
                profileName = includePath.Split(':')[1];

                // Set output path
                includePath = includePath.Split(':')[0];
            }

            // If the profile is blank, always include it
            if (string.IsNullOrEmpty(profileName) ||
                // Or if we specify ! only include it if the specified profile is  blank
                (profileName == "!" && string.IsNullOrEmpty(output.ProfileName)) ||
                // Or if the profile name matches include it
                string.Equals(output.ProfileName, profileName, StringComparison.CurrentCultureIgnoreCase))
            {
                // Try and find the include file
                var includedContents = FindIncludeFile(data.FullPath, includePath, out string resolvedPath);

                // If we didn't find it, error out
                if (includedContents == null)
                {
                    data.Error = $"Include file not found {includePath}";
                    return;
                }

                // Otherwise we got it, so replace the tag with the contents
                ReplaceTag(output, match, includedContents, removeNewline: false);
            }
            // Remove include tag and finish
            else
            {
                ReplaceTag(output, match, string.Empty);
            }
        }

        /// <summary>
        /// Processes the variable and data tags in the list and edits the files contents as required
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <returns></returns>
        protected void ProcessDataTags(FileProcessingData data)
        {
            // For each output
            data.OutputPaths.ForEach(output =>
            {
                // Find all sets of XML data that contain variables and other data
                var match = Regex.Match(output.FileContents, mStandardVariableRegex, RegexOptions.Singleline);

                // No error to start with
                data.Error = string.Empty;

                // Loop through all matches
                while (match.Success)
                {
                    // NOTE: The first group is the full match
                    //       The second group is the XML

                    // Make sure we have enough groups
                    if (match.Groups.Count < 2)
                    {
                        data.Error = $"Malformed match {match.Value}";
                        return;
                    }

                    // Take the first match as the header for the type of tag
                    var xmlString = match.Groups[1].Value.Trim();

                    // Create XDocument
                    XDocument xmlData = null;

                    try
                    {
                        // Get XML data from it
                        xmlData = XDocument.Parse(xmlString);
                    }
                    catch (Exception ex)
                    {
                        data.Error = $"Malformed data region {xmlString}. {ex.Message}";
                        return;
                    }

                    // Extract variables and any other data from it
                    ExtractData(data, output, xmlData);

                    // If it failed, return now
                    if (!data.Successful)
                        return;

                    // Remove tag
                    ReplaceTag(output, match, string.Empty);

                    // Find the next data region
                    match = Regex.Match(output.FileContents, mStandardVariableRegex, RegexOptions.Singleline);
                }
            });
        }

        #region Private Helpers

        /// <summary>
        /// Searches for an input file in certain locations relative to the input file
        /// and returns the contents of it if found. 
        /// Returns null if not found
        /// </summary>
        /// <param name="path">The input path of the orignal file</param>
        /// <param name="includePath">The include path of the file trying to be included</param>
        /// <param name="resolvedPath">The resolved full path of the file that was found to be included</param>
        /// <param name="returnContents">True to return the files actual contents, false to return an empty string if found and null otherwise</param>
        /// <returns></returns>
        protected string FindIncludeFile(string path, string includePath, out string resolvedPath, bool returnContents = true)
        {
            // No path yet
            resolvedPath = null;

            // First look in the same folder
            var foundPath = Path.Combine(Path.GetDirectoryName(path), includePath);

            // If we found it, return contents
            if (FileManager.FileExists(foundPath))
            {
                // Set the resolved path
                resolvedPath = foundPath;

                // Return the contents
                return returnContents ? File.ReadAllText(foundPath) : string.Empty;
            }

            // Not found
            return null;
        }

        /// <summary>
        /// Replaces a given Regex match with the contents
        /// </summary>
        /// <param name="data">The file output data</param>
        /// <param name="match">The regex match to replace</param>
        /// <param name="newContent">The content to replace the match with</param>
        /// <param name="removeNewline">Remove the newline following the match if one is present</param>
        protected void ReplaceTag(FileOutputData data, Match match, string newContent, bool removeNewline = true)
        {
            var contents = data.FileContents;
            ReplaceTag(ref contents, match, newContent, removeNewline);
            data.FileContents = contents;
        }

        /// <summary>
        /// Replaces a given Regex match with the contents
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="match">The regex match to replace</param>
        /// <param name="newContent">The content to replace the match with</param>
        /// <param name="removeNewline">Remove the newline following the match if one is present</param>
        protected void ReplaceTag(FileProcessingData data, Match match, string newContent, bool removeNewline = true)
        {
            var contents = data.UnprocessedFileContents;
            ReplaceTag(ref contents, match, newContent, removeNewline);
            data.UnprocessedFileContents = contents;
        }

        /// <summary>
        /// Replaces a given Regex match with the contents
        /// </summary>
        /// <param name="fileContents">The file contents</param>
        /// <param name="match">The regex match to replace</param>
        /// <param name="newContent">The content to replace the match with</param>
        /// <param name="removeNewline">Remove the newline following the match if one is present</param>
        protected void ReplaceTag(ref string fileContents, Match match, string newContent, bool removeNewline = true)
        {
            // If we want to remove a suffixed newline...
            if (removeNewline)
            {
                // Remove carriage return
                if ((fileContents.Length > match.Index + match.Length) &&
                    fileContents[match.Index + match.Length] == '\r')
                    fileContents = string.Concat(fileContents.Substring(0, match.Index + match.Length), fileContents.Substring(match.Index + match.Length + 1));

                // Return newline
                if ((fileContents.Length > match.Index + match.Length) &&
                    fileContents[match.Index + match.Length] == '\n')
                    fileContents = string.Concat(fileContents.Substring(0, match.Index + match.Length), fileContents.Substring(match.Index + match.Length + 1));
            }

            // If the match is at the start, replace it
            if (match.Index == 0)
                fileContents = newContent + fileContents.Substring(match.Length);
            // Otherwise do an inner replace
            else
                fileContents = string.Concat(fileContents.Substring(0, match.Index), newContent, fileContents.Substring(match.Index + match.Length));
        }

        #endregion

        #endregion

        #region Generate Output

        /// <summary>
        /// Replaces all variables with the variable values and generates the final output data
        /// for a given output path
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="output">The output data to generate the contents for</param>
        protected void GenerateOutput(FileProcessingData data, FileOutputData output)
        {
            // Create a match variable
            Match match = null;

            // Get a copy of the contents
            var contents = output.FileContents;

            // Go though all matches
            while (match == null || match.Success)
            {
                // Find all variables
                match = Regex.Match(contents, mVariableUseRegex);

                // Make sure we have enough groups
                if (match.Groups.Count < 2)
                    continue;

                // NOTE: Group 0 = $$VariableHere$$
                //       Group 1 = VariableHere

                // Get variable without the surrounding tags $$
                var variable = match.Groups[1].Value;

                // Check if we have a Dna Variable
                if (variable.StartsWith(mDnaVariablePrefix))
                {
                    // If it fails to process, return now (don't output)
                    if (!ProcessDnaVariable(data, output, match, ref contents))
                        return;
                }
                // Otherwise...
                else
                {
                    // If it fails to process, return now (don't output)
                    if (!ProcessVariable(data, output, match, ref contents))
                        return;
                }
            }

            // Set results
            output.CompiledContents = contents;
        }

        /// <summary>
        /// Processes the Dna variable and converts it into the resolved value
        /// </summary>
        /// <param name="data">The processing data</param>
        /// <param name="output">The file output data</param>
        /// <param name="match">The regex match that found this variable</param>
        /// <param name="contents">The files original contents</param>
        private bool ProcessDnaVariable(FileProcessingData data, FileOutputData output, Match match, ref string contents)
        {
            // Get the variable name (without the dna. prefix as well)
            var variable = match.Groups[1].Value.Substring(mDnaVariablePrefix.Length);

            // Date Time
            var dateMatch = Regex.Match(contents, mDnaVariableDateRegex, RegexOptions.Singleline);
            if (dateMatch.Success && dateMatch.Groups.Count >= 2)
            {
                // Get the string format from inside Date("")
                var dateFormat = dateMatch.Groups[1].Value;

                // Now try and replace the value, catching any unexpected errors
                return TryProcessDnaVariable(data, output, match, ref contents, variable, () =>
                {
                    return DateTime.Now.ToString(dateFormat);
                });
            }
            // Project Path
            else if (string.Equals(variable, mDnaVariableProjectPath, StringComparison.InvariantCultureIgnoreCase))
            {
                // Now try and replace the value, catching any unexpected errors
                return TryProcessDnaVariable(data, output, match, ref contents, variable, () =>
                {
                    return Environment.CurrentDirectory;
                });
            }
            // File Path
            else if (string.Equals(variable, mDnaVariableFilePath, StringComparison.InvariantCultureIgnoreCase))
            {
                // Now try and replace the value, catching any unexpected errors
                return TryProcessDnaVariable(data, output, match, ref contents, variable, () =>
                {
                    return data.FullPath;
                });
            }
            // Error if we don't get one
            else
            {
                data.Error = $"Dna Variable not found {variable}";

                // Clear contents as the output will be invalid now if it is not processable
                output.CompiledContents = null;

                return false;
            }
        }

        /// <summary>
        /// Tries to process a Dna Variable, catching any unexpected errors and handling them nicely
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="output">The file output data</param>
        /// <param name="match">The regex match that foudn this Dna variable</param>
        /// <param name="contents">The current file contents</param>
        /// <param name="variable">The variable name being processed</param>
        /// <param name="process">The action to run</param>
        /// <returns></returns>
        private bool TryProcessDnaVariable(FileProcessingData data, FileOutputData output, Match match, ref string contents, string variable, Func<string> process)
        {
            try
            {
                // Try and get the expected value to replace from the action
                var result = process();

                // Now replace it
                ReplaceTag(ref contents, match, result);

                return true;
            }
            catch (Exception ex)
            {
                data.Error = $"Unexpected error processing Dna Variable {variable}.{Environment.NewLine}{ex.Message}";

                // Clear contents as the output will be invalid now if it is not processable
                output.CompiledContents = null;

                return false;
            }
        }

        /// <summary>
        /// Processes the variable and converts it into the value of that variable
        /// </summary>
        /// <param name="data">The processing data</param>
        /// <param name="output">The file output data</param>
        /// <param name="match">The regex match that found this variable</param>
        /// <param name="contents">The files original contents</param>
        private bool ProcessVariable(FileProcessingData data, FileOutputData output, Match match, ref string contents)
        {
            // Get the variable name
            var variable = match.Groups[1].Value;

            // Resolve the name to a variable value stored in the output variables
            var variableValue = output.Variables.FirstOrDefault(v =>
                string.Equals(v.Name, variable, StringComparison.CurrentCultureIgnoreCase) &&
                string.Equals(v.ProfileName, output.ProfileName, StringComparison.CurrentCultureIgnoreCase));

            // If this was a profile-specific variable, fallback to the standard variable 
            if (variableValue == null && !string.IsNullOrEmpty(output.ProfileName))
                variableValue = output.Variables.FirstOrDefault(v =>
                    string.Equals(v.Name, variable, StringComparison.CurrentCultureIgnoreCase) &&
                    string.IsNullOrEmpty(v.ProfileName));

            // Error if we don't get one
            if (variableValue == null)
            {
                data.Error = $"Variable not found {variable} for profile '{output.ProfileName}'";

                // Clear contents as the output will be invalid now if it is not processable
                output.CompiledContents = null;
                return false;
            }

            // Replace with the value
            ReplaceTag(ref contents, match, variableValue?.Value);

            return true;
        }

        /// <summary>
        /// Changes the file extension to the default output file extension
        /// </summary>
        /// <param name="path">The full path to the file</param>
        /// <returns></returns>
        protected string GetDefaultOutputPath(string path)
        {
            return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + OutputExtension);
        }

        #endregion

        #region Extract Data Regions

        /// <summary>
        /// Extracts the data from an <see cref="XDocument"/> and stores the variables and data in the engine
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="output">The file output data</param>
        /// <param name="xmlData">The data to extract</param>
        protected void ExtractData(FileProcessingData data, FileOutputData output, XDocument xmlData)
        {
            // Find any variables
            ExtractVariables(data, output, xmlData.Root);

            // Profiles
            foreach (var profileElement in xmlData.Root.Elements("Profile"))
            {
                // Find any variables
                ExtractVariables(data, output, profileElement, profileName: profileElement.Attribute("Name")?.Value);
            }

            // Groups
            foreach (var groupElement in xmlData.Root.Elements("Group"))
            {
                // Find any variables
                ExtractVariables(data, output, groupElement, profileName: groupElement.Attribute("Profile")?.Value, groupName: groupElement.Attribute("Name")?.Value);
            }
        }

        /// <summary>
        /// Extract variables from the XML element
        /// </summary>
        /// <param name="data">The file processing data</param>
        /// <param name="output">The file output data</param>
        /// <param name="element">The Xml element</param>
        /// <param name="profileName">The profile name, if explicitly specified</param>
        protected void ExtractVariables(FileProcessingData data, FileOutputData output, XElement element, string profileName = null, string groupName = null)
        {
            // Loop all elements with the name of variable
            foreach (var variableElement in element.Elements("Variable"))
            {
                try
                {
                    // Create the variable
                    var variable = new EngineVariable
                    {
                        XmlElement = variableElement,
                        Name = variableElement.Attribute("Name")?.Value,
                        ProfileName = variableElement.Attribute("Profile")?.Value ?? profileName,
                        Group = variableElement.Attribute("Group")?.Value ?? groupName,
                        Value = variableElement.Element("Value")?.Value ?? variableElement.Value,
                        Comment = variableElement.Element("Comment")?.Value ?? variableElement.Attribute("Comment")?.Value
                    };

                    // Convert string empty profile names back to null
                    if (variable.ProfileName == string.Empty)
                        variable.ProfileName = null;

                    // If we have no comment, look at previous element for a comment
                    if (string.IsNullOrEmpty(variable.Comment) && variableElement.PreviousNode is XComment)
                        variable.Comment = ((XComment)variableElement.PreviousNode).Value;

                    // Make sure we got a name at least
                    if (string.IsNullOrEmpty(variable.Name))
                    {
                        data.Error = $"Variable has no name {variableElement}";
                        break;
                    }

                    // Add or update variable
                    var existing = output.Variables.FirstOrDefault(f =>
                        string.Equals(f.Name, variable.Name) &&
                        string.Equals(f.ProfileName, variable.ProfileName));

                    // If one exists, update it
                    if (existing != null)
                        existing.Value = variable.Value;
                    // Otherwise, add it
                    else
                        output.Variables.Add(variable);
                }
                catch (Exception ex)
                {
                    data.Error = $"Unexpected error parsing variable {variableElement}. {ex.Message}";

                    // No more processing
                    break;
                }
            }
        }

        #endregion

        #region Find References

        /// <summary>
        /// Searches the <see cref="MonitorPath"/> for all files that match the <see cref="EngineExtensions"/> 
        /// then searches inside them to see if they include the includePath passed in />
        /// </summary>
        /// <param name="includePath">The path to look for being included in any of the files</param>
        /// <returns></returns>
        protected async Task<List<string>> FindReferencedFilesAsync(string includePath)
        {
            // New empty list
            var toProcess = new List<string>();

            // If we have no path, return 
            if (string.IsNullOrWhiteSpace(includePath))
                return toProcess;

            // Find all files in the monitor path
            var allFiles = FindAllFiles();

            // For each file, find all resolved references
            foreach (var file in allFiles)
            {
                // Get all resolved references
                var references = await GetResolvedIncludePathsAsync(file);

                // If any match this file...
                if (references.Any(reference => string.Equals(reference, includePath, StringComparison.CurrentCultureIgnoreCase)))
                    // Add this file to be processed
                    toProcess.Add(file);
            };

            // Return what we found
            return toProcess;
        }

        /// <summary>
        /// Finds all files in the monitor folder that match this engines extension types
        /// </summary>
        /// <returns></returns>
        private List<string> FindAllFiles()
        {
            // New empty list
            var foundFiles = new List<string>();

            return GetDirectoryFiles(MonitorPath, "*.*")
                           .Where(file => EngineExtensions.Any(ex => Regex.IsMatch(Path.GetFileName(file), ex)))
                           .ToList();
        }


        /// <summary>
        /// A safe way to get all the files in a directory and sub directory without crashing on UnauthorizedException or PathTooLongException
        /// </summary>
        /// <param name="rootPath">Starting directory</param>
        /// <param name="patternMatch">Filename pattern match</param>
        /// <param name="searchOption">Search subdirectories or only top level directory for files</param>
        /// <returns>List of files</returns>
        public static IEnumerable<string> GetDirectoryFiles(string rootPath, string patternMatch)
        {
            // List of found files
            var foundFiles = Enumerable.Empty<string>();

            try
            {
                // Get all directories in this path
                var directories = Directory.EnumerateDirectories(rootPath);

                // For each sub-directory...
                foreach (var directory in directories)
                    // Add files in subdirectories recursively to the list
                    foundFiles = foundFiles.Concat(GetDirectoryFiles(directory, patternMatch));
            }
            // Catch the exceptions we want to ignore
            catch (UnauthorizedAccessException) { }
            catch (PathTooLongException) { }

            // Add files from the current directory
            try { foundFiles = foundFiles.Concat(Directory.EnumerateFiles(rootPath, patternMatch)); }
            catch (UnauthorizedAccessException) { }

            // Return results
            return foundFiles;
        }


        /// <summary>
        /// Returns a list of resolved paths for all include files in a file
        /// </summary>
        /// <param name="filePath">The full path to the file to check</param>
        /// <returns></returns>
        protected async Task<List<string>> GetResolvedIncludePathsAsync(string filePath)
        {
            // New blank list
            var paths = new List<string>();

            // Make sure the file exists
            if (!FileManager.FileExists(filePath))
                return paths;

            // Read all the file into memory (it's ok we will never have large files they are text web files)
            var fileContents = await FileManager.ReadAllText(filePath);

            // Create a match variable
            Match match = null;

            // Go though all matches
            while (match == null || match.Success)
            {
                // If we have already run a match once...
                if (match != null)
                    // Remove previous tag and carry on
                    ReplaceTag(ref fileContents, match, string.Empty);

                // Find all special tags that have 2 groups
                match = Regex.Match(fileContents, mStandard2GroupRegex, RegexOptions.Singleline);

                // Make sure we have enough groups
                if (match.Groups.Count < 3)
                    continue;

                // Make sure this is an include
                if (!string.Equals(match.Groups[1].Value, "include"))
                    continue;

                // Get include path
                var includePath = match.Groups[2].Value;

                // Strip any profile name (we don't care about that for this)
                // If the name have a single : then the right half is the profile name
                if (includePath.Count(c => c == ':') == 1)
                    includePath = includePath.Split(':')[0];

                // Resolve any relative aspects of the path
                if (!Path.IsPathRooted(includePath))
                    includePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filePath), includePath));

                // Try and find the include file
                FindIncludeFile(filePath, includePath, out string resolvedPath, returnContents: false);

                // Add the resolved path if we got one
                if (!string.IsNullOrEmpty(resolvedPath))
                    paths.Add(resolvedPath);
            }

            // Return the results
            return paths;
        }

        #endregion

        #region Logger

        /// <summary>
        /// Logs a message and raises the <see cref="LogMessage"/> event
        /// </summary>
        /// <param name="title">The title of the log</param>
        /// <param name="message">The main message of the log</param>
        /// <param name="type">The type of the log message</param>
        public void Log(string title, string message = "", LogType type = LogType.Diagnostic)
        {
            LogMessage(new LogMessage
            {
                Title = title,
                Message = message,
                Time = DateTime.UtcNow,
                Type = type
            });
        }

        /// <summary>
        /// Logs a message and raises the <see cref="LogMessage"/> event
        /// Logs a name and value at a certain tab level to simulate a sub-item of another log
        /// 
        /// For example: LogTabbed("Name", "Value", 1);
        /// ----Name: Value
        /// Where ---- are spaces
        /// </summary>
        /// <param name="name">The name to log</param>
        /// <param name="value">The value to log</param>
        /// <param name="tabLevel"></param>
        /// <param name="type">The type of the log message</param>
        public void LogTabbed(string name, string value, int tabLevel, LogType type = LogType.Diagnostic)
        {
            // Add 4 spaces per tab level
            Log($"{"".PadLeft(tabLevel * 4, ' ')}{name}: {value}", type: type);
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            // Log the message
            if (mWatchers != null)
                Log($"{EngineName} Engine stopped");

            // Clean up all file watchers
            mWatchers?.ForEach(watcher =>
            {
                // Get extension
                var extension = watcher.Filter;

                // Dispose of watcher
                watcher.Dispose();

                // Inform listener
                StoppedWatching(extension);

                // Log the type
                LogTabbed("File Type", watcher.Filter, 1);
            });

            // Space between each engine log
            Log("");

            // Let listener know we stopped
            if (mWatchers != null)
                Stopped();

            // Clear watchers
            mWatchers = null;
        }

        #endregion
    }
}