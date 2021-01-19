﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using AspNetMigrator.ConfigUpdater;
using Microsoft.Build.Evaluation;
using Microsoft.Extensions.Logging;

namespace AspNetMigrator.DefaultConfigUpdaters
{
    public class AppSettingsMigrator : IConfigUpdater
    {
        private const string AppSettingsPath = "/configuration/appSettings";
        private const string AddSettingElementName = "add";
        private const string KeyAttributeName = "key";
        private const string ValueAttributeName = "value";
        private const string AppSettingsJsonFileName = "appsettings.json";

        private static readonly Regex AppSettingsFileRegex = new("^appsettings(\\..+)?\\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly ILogger<AppSettingsMigrator> _logger;
        private readonly Dictionary<string, string> _appSettingsToMigrate;

        public string Title => "Migrate appSettings";

        public string Description => "Migrate app settings from app.config and web.config files to appsettings.json";

        public BuildBreakRisk Risk => BuildBreakRisk.Low;

        public AppSettingsMigrator(ILogger<AppSettingsMigrator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettingsToMigrate = new Dictionary<string, string>();
        }

        public async Task<bool> ApplyAsync(IMigrationContext context, ImmutableArray<ConfigFile> configFiles, CancellationToken token)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var project = await context.GetProjectAsync(token).ConfigureAwait(false);
            if (project is null)
            {
                _logger.LogError("No project loaded");
                return false;
            }

            // Determine where appsettings.json should live
            var appSettingsPath = Path.Combine(project.Directory ?? string.Empty, AppSettingsJsonFileName);

            // Parse existing appsettings.json properties, if any
            var existingJson = "{}";
            if (File.Exists(appSettingsPath))
            {
                // Read all text instead of keeping the stream open so that we can
                // re-open the config file later in this method as writeable
                existingJson = await File.ReadAllTextAsync(appSettingsPath, token).ConfigureAwait(false);
            }

            using var json = JsonDocument.Parse(existingJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var existingProperties = json.RootElement.EnumerateObject();

            // Write an updated appsettings.json file including the previous properties and new ones for the new app settings properties
            using var fs = new FileStream(appSettingsPath, FileMode.Create, FileAccess.Write);
            using var jsonWriter = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            jsonWriter.WriteStartObject();
            foreach (var property in existingProperties)
            {
                property.WriteTo(jsonWriter);
            }

            foreach (var setting in _appSettingsToMigrate)
            {
                if (bool.TryParse(setting.Value, out var boolValue))
                {
                    jsonWriter.WriteBoolean(setting.Key, boolValue);
                }
                else if (long.TryParse(setting.Value, out var longValue))
                {
                    jsonWriter.WriteNumber(setting.Key, longValue);
                }
                else if (double.TryParse(setting.Value, out var doubleValue))
                {
                    jsonWriter.WriteNumber(setting.Key, doubleValue);
                }
                else
                {
                    jsonWriter.WriteString(setting.Key, setting.Value);
                }
            }

            jsonWriter.WriteEndObject();

            // Make sure the project is reloaded in case the appsettings.json file was added in this apply step
            await context.ReloadWorkspaceAsync(token).ConfigureAwait(false);

            // Confirm that the appsettings.json file is included in the project. In rare cases (auto-include disabled),
            // it may be necessary to add it explicitly
            if (project.ContainsItem(appSettingsPath, ProjectItemType.Content, token))
            {
                var projectRoot = await context.GetProjectRootElementAsync(token).ConfigureAwait(false);
                projectRoot.AddItem(ProjectItemType.Content.Name, appSettingsPath);
                projectRoot.Save();
            }

            return true;
        }

        public async Task<bool> IsApplicableAsync(IMigrationContext context, ImmutableArray<ConfigFile> configFiles, CancellationToken token)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // Find appSettings elements in the config files
            var appSettings = new Dictionary<string, string>();
            foreach (var configFile in configFiles)
            {
                var appSettingsElement = configFile.Contents.XPathSelectElement(AppSettingsPath);
                if (appSettingsElement is not null)
                {
                    foreach (var setting in appSettingsElement.Elements(AddSettingElementName))
                    {
                        if (setting is not null)
                        {
                            var key = setting.Attribute(KeyAttributeName);
                            var value = setting.Attribute(ValueAttributeName);
                            if (key is not null && value is not null)
                            {
                                _logger.LogDebug("Found app setting {AppSettingName} in {ConfigFilePath}", key.Value, configFile.Path);
                                appSettings[key.Value] = value.Value;
                            }
                        }
                    }
                }
            }

            // Check for existing appSettings.json files for app settings
            using var projectCollection = new ProjectCollection();
            var project = projectCollection.LoadProject(await context.GetProjectPathAsync(token).ConfigureAwait(false));
            var jsonConfigFiles = await GetAppSettingsConfigFilesAsync(project, token).ConfigureAwait(false);

            foreach (var setting in appSettings)
            {
                if (!jsonConfigFiles.Values.Any(s => ConfigFileContainsElement(s, setting.Key)))
                {
                    _appSettingsToMigrate.Add(setting.Key, setting.Value);
                }
                else
                {
                    _logger.LogDebug("Existing app settings already include setting {SettingName}", setting.Key);
                }
            }

            _logger.LogInformation("Found {AppSettingCount} app settings for migration: {AppSettingNames}", _appSettingsToMigrate.Count, string.Join(", ", _appSettingsToMigrate.Keys));

            return _appSettingsToMigrate.Count > 0;
        }

        private static bool ConfigFileContainsElement(JsonDocument doc, string elementName) =>
            doc.RootElement.EnumerateObject().Any(p => p.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase));

        private static async Task<Dictionary<string, JsonDocument>> GetAppSettingsConfigFilesAsync(Project project, CancellationToken token)
        {
            var ret = new Dictionary<string, JsonDocument>();
            foreach (var configFile in project.Items.Where(i => i.ItemType.Equals(ProjectItemType.Content.Name, StringComparison.OrdinalIgnoreCase) && AppSettingsFileRegex.IsMatch(Path.GetFileName(i.EvaluatedInclude))))
            {
                var projectDir = Path.GetDirectoryName(project.FullPath)!;
                var filePath = Path.IsPathFullyQualified(configFile.EvaluatedInclude) ?
                    configFile.EvaluatedInclude :
                    Path.Combine(projectDir, configFile.EvaluatedInclude);

                if (!File.Exists(filePath) || ret.ContainsKey(filePath))
                {
                    continue;
                }

                var jsonOptions = new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                };
                using var fs = File.OpenRead(filePath);
                ret.Add(filePath, await JsonDocument.ParseAsync(fs, jsonOptions, token).ConfigureAwait(false));
            }

            return ret;
        }
    }
}