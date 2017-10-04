﻿using Newtonsoft.Json;
using System;
using System.IO;

namespace Dna.Web.Core
{
    /// <summary>
    /// The configuration for a Dna Web environment
    /// </summary>
    public class DnaConfiguration
    {
        #region Public Properties

        /// <summary>
        /// The paths to monitor for files
        /// </summary>
        [JsonProperty(PropertyName = DnaSettings.ConfigurationNameMonitorPath)]
        public string MonitorPath { get; set; } = ".";

        /// <summary>
        /// The generate files processing option on start up
        /// </summary>
        [JsonProperty(PropertyName = DnaSettings.ConfigurationNameGenerateOnStart)]
        public GenerateOption? GenerateOnStart { get; set; } = GenerateOption.None;

        /// <summary>
        /// True if the engine should spin up, process and then close without waiting for user input
        /// </summary>
        [JsonProperty(PropertyName = DnaSettings.ConfigurationNameProcessAndClose)]
        public bool? ProcessAndClose { get; set; } = false;

        #endregion

        #region Public Static Helpers

        /// <summary>
        /// Attempts to load a <see cref="DnaConfiguration"/> from a configuration file
        /// </summary>
        /// <param name="filePath">The path to the configuration file</param>
        public static DnaConfiguration LoadFromFile(string filePath)
        {
            try
            {
                // Make sure file exists
                if (!File.Exists(filePath))
                {
                    // Log error
                    CoreLogger.Log($"Config File Not Found: {filePath}");

                    // Return nothing
                    return null;
                }

                // Json deserialize
                return JsonConvert.DeserializeObject<DnaConfiguration>(File.ReadAllText(filePath));
            }
            catch (Exception ex)
            {
                // Log error
                CoreLogger.Log($"Failed to load configuration file: {filePath}. {ex.Message}", type: LogType.Error);

                return null;
            }
        }

        /// <summary>
        /// Attempts to load a <see cref="DnaConfiguration"/> from a set of configuration files
        /// The order of priority in the list is first is least priority, last is highest.
        /// 
        /// Any values coming after will override the previous values, to create a final
        /// combined <see cref="DnaConfiguration"/> 
        /// </summary>
        /// <param name="filePaths">A list of all paths to the configuration files</param>
        public static DnaConfiguration LoadFromFiles(string[] filePaths)
        {
            // Create final setting as default
            var finalSetting = new DnaConfiguration();

            // For each file
            foreach (var filePath in filePaths)
            {
                // Try and load the settings
                var settings = LoadFromFile(filePath);

                // TODO: Update to use reflection on properties and set the finalSettings
                //       if the settings properties are not null

                // Make sure we got settings
                if (settings == null)
                    continue;

                CoreLogger.Log($"Configuration: {filePath}");

                // Monitor Path
                if (!string.IsNullOrEmpty(settings.MonitorPath))
                {
                    // Set value
                    finalSetting.MonitorPath = settings.MonitorPath;

                    // Log it
                    CoreLogger.LogTabbed("Monitor", finalSetting.MonitorPath, 1);
                }

                // Generate On Start
                if (settings.GenerateOnStart.HasValue)
                {
                    // Set value
                    finalSetting.GenerateOnStart = settings.GenerateOnStart;

                    // Log it
                    CoreLogger.LogTabbed("GenerateOnStart", finalSetting.GenerateOnStart.ToString(), 1);
                }

                // Process and Close
                if (settings.ProcessAndClose.HasValue)
                {
                    // Set value
                    finalSetting.ProcessAndClose = settings.ProcessAndClose;

                    // Log it
                    CoreLogger.LogTabbed("ProcessAndClose", finalSetting.ProcessAndClose.ToString(), 1);
                }

                // Space between each configuration details for console log niceness
                CoreLogger.Log("");
            }

            // Return the result
            return finalSetting;
        }
        #endregion
    }
}