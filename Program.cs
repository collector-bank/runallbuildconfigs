using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RunAllBuildConfigs
{
    class Program
    {
        class Buildstep
        {
            public string Stepid { get; set; }
            public string Stepname { get; set; }
            public string Steptype { get; set; }
            public bool? Disabled { get; set; }
            public bool Disable { get; set; }
        }

        class Build
        {
            public string Buildid { get; set; }
            public List<Buildstep> Steps { get; set; }
            public Dictionary<string, string> Properties { get; set; }
            public bool DontRun { get; set; }
        }

        static bool _buildDebug;
        static int RestDebugCount = 0;
        static readonly Dictionary<string, bool> _writtenLogs = new Dictionary<string, bool>();

        static int Main(string[] args)
        {
            int result = 0;
            if (args.Length != 0 && (args.Length != 1 || !args[0].StartsWith("@")))
            {
                Console.WriteLine(
@"RunAllBuildConfigs 0.009 - Trigger all builds.

Usage: RunAllBuildConfigs.exe

Environment variables:
BuildServer
BuildUsername
BuildPassword
TEAMCITY_BUILD_PROPERTIES_FILE (can retrieve the 3 above: Server, Username, Password)

Optional environment variables:
BuildAdditionalParameters
BuildDisableBuildSteps
BuildDisableBuildStepTypes
BuildDryRun
BuildExcludeBuildConfigs
BuildExcludeBuildStepTypes
BuildSortedExecution
BuildDebug
BuildVerbose");
                result = 1;
            }
            else
            {
                try
                {
                    TriggerMeEasy();
                }
                catch (ApplicationException ex)
                {
                    LogTCError(ex.Message);
                    result = 1;
                }
                catch (Exception ex)
                {
                    LogTCError(ex.ToString());
                    result = 1;
                }
            }

            if (Environment.UserInteractive && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DontPrompt")))
            {
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }

            return result;
        }

        static void TriggerMeEasy()
        {
            _buildDebug = GetBooleanEnvironmentVariable("BuildDebug", false);

            bool dryRun = GetBooleanEnvironmentVariable("BuildDryRun", false);
            bool sortedExecution = GetBooleanEnvironmentVariable("BuildSortedExecution", true);
            bool verbose = GetBooleanEnvironmentVariable("BuildVerbose", false);

            string server = GetServer();

            GetCredentials(out string username, out string password);

            var additionalParameters = GetDictionaryEnvironmentVariable("BuildAdditionalParameters", null);

            string[] disabledBuildSteps = GetStringArrayEnvironmentVariable("BuildDisableBuildSteps", null);
            string[] disabledBuildStepTypes = GetStringArrayEnvironmentVariable("BuildDisableBuildStepTypes", null);
            string[] excludedBuildConfigs = GetStringArrayEnvironmentVariable("BuildExcludeBuildConfigs", null);
            string[] excludedBuildStepTypes = GetStringArrayEnvironmentVariable("BuildExcludeBuildStepTypes", null);

            var builds = LogTCSection("Retrieving build configs and steps", () =>
            {
                return GetBuildConfigs(server, username, password);
            });

            foreach (Build build in builds)
            {
                build.Properties = additionalParameters;
            }

            int totalbuilds = builds.Count;
            int totalsteps = builds.Sum(b => b.Steps.Count);
            int enabledsteps = builds.Sum(b => b.Steps.Count(s => (!s.Disabled.HasValue || !s.Disabled.Value) && !s.Disable));

            Log($"Found {totalbuilds} build configs, with {enabledsteps}/{totalsteps} enabled build steps.", ConsoleColor.Green);

            if (sortedExecution)
            {
                builds = builds.OrderBy(b => b.Buildid, StringComparer.OrdinalIgnoreCase).ToList();
            }

            ExcludeBuildStepTypes(builds, excludedBuildStepTypes);

            ExcludeMe(builds);

            ExcludedBuildConfigs(builds, excludedBuildConfigs);

            DisableBuildSteps(builds, disabledBuildSteps);

            DisableBuildStepTypes(builds, disabledBuildStepTypes);

            if (verbose)
            {
                LogTCSection("Build configs and steps", () =>
                {
                    PrintBuildSteps(builds);
                });
            }


            int dontrunbuilds = builds.Count(b => b.DontRun);
            int disablesteps = builds.Sum(b => b.Steps.Count(s => s.Disable));

            totalbuilds = builds.Count;
            int enabledbuilds = builds.Count(b => !b.DontRun);
            totalsteps = builds.Sum(b => b.Steps.Count);
            enabledsteps = builds.Sum(b => b.Steps.Count(s => (!s.Disabled.HasValue || !s.Disabled.Value) && !s.Disable));


            Log($"Excluding {dontrunbuilds} builds configs.", ConsoleColor.Green);
            Log($"Disabling {disablesteps} additional build steps.", ConsoleColor.Green);
            Log($"Triggering {enabledbuilds}/{totalbuilds} build configs, with {enabledsteps}/{totalsteps} enabled build steps...", ConsoleColor.Green);

            TriggerBuilds(server, username, password, builds, dryRun);
        }

        static void ExcludeBuildStepTypes(List<Build> builds, string[] excludedBuildStepTypes)
        {
            if (excludedBuildStepTypes != null && excludedBuildStepTypes.Length > 0)
            {
                var excludes = builds
                    .Where(b => b.Steps.Any(s => (!s.Disabled.HasValue || !s.Disabled.Value) && excludedBuildStepTypes.Any(ss => ss == s.Steptype)))
                    .ToArray();
                Log($"Excluding {excludes.Length} build configs (steptype).");
                foreach (var build in excludes)
                {
                    List<string> excludesteps = build.Steps
                        .Where(s => (!s.Disabled.HasValue || !s.Disabled.Value) && excludedBuildStepTypes.Any(ss => ss == s.Steptype))
                        .Select(s => $"{s.Stepname}|{s.Steptype}")
                        .ToList();
                    string reason = string.Join(", ", excludesteps);
                    Log($"Excluding build config: '{build.Buildid}' ({reason})");
                    build.DontRun = true;
                }
            }
        }

        static void ExcludeMe(List<Build> builds)
        {
            var tcvariables = GetTeamcityBuildVariables();
            if (tcvariables.ContainsKey("teamcity.buildType.id"))
            {
                string buildConfig = tcvariables["teamcity.buildType.id"];
                var excludes = builds.Where(b => b.Buildid.Equals(buildConfig, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (excludes.Length > 0)
                {
                    foreach (Build build in excludes)
                    {
                        Log($"Excluding build config (me): '{build.Buildid}'");
                        build.DontRun = true;
                    }
                }
                else
                {
                    LogTCWarning($"Could not exclude build config (me): {buildConfig}");
                }
            }
        }

        static void ExcludedBuildConfigs(List<Build> builds, string[] excludedBuildConfigs)
        {
            if (excludedBuildConfigs != null)
            {
                foreach (string buildConfig in excludedBuildConfigs)
                {
                    var excludes = builds.Where(b => b.Buildid.Equals(buildConfig, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (excludes.Length > 0)
                    {
                        foreach (Build build in excludes)
                        {
                            Log($"Excluding build config: '{build.Buildid}'");
                            build.DontRun = true;
                        }
                    }
                    else
                    {
                        LogTCWarning($"Could not exclude build config: {buildConfig}");
                    }
                }
            }
        }

        static void DisableBuildSteps(List<Build> builds, string[] disabledBuildSteps)
        {
            if (disabledBuildSteps != null)
            {
                foreach (Build build in builds)
                {
                    foreach (Buildstep buildstep in build.Steps.Where(s => (!s.Disabled.HasValue || !s.Disabled.Value) && disabledBuildSteps.Contains(s.Stepid)))
                    {
                        buildstep.Disable = true;
                    }
                }
            }
        }

        static void DisableBuildStepTypes(List<Build> builds, string[] disabledBuildStepTypes)
        {
            if (disabledBuildStepTypes != null)
            {
                foreach (Build build in builds)
                {
                    foreach (Buildstep buildstep in build.Steps.Where(s => (!s.Disabled.HasValue || !s.Disabled.Value) && disabledBuildStepTypes.Contains(s.Steptype)))
                    {
                        buildstep.Disable = true;
                    }
                }
            }
        }

        static string GetServer()
        {
            string server = Environment.GetEnvironmentVariable("BuildServer");

            if (server != null)
            {
                Log($"Got server from environment variable: '{server}'");
            }

            if (server == null)
            {
                var tcvariables = GetTeamcityConfigVariables();

                if (server == null && tcvariables.ContainsKey("teamcity.serverUrl"))
                {
                    server = tcvariables["teamcity.serverUrl"];
                    Log($"Got server from Teamcity: '{server}'");
                }
            }

            if (server == null)
            {
                throw new ApplicationException("No server specified.");
            }
            else
            {
                if (!server.StartsWith("http://") && !server.StartsWith("https://"))
                {
                    server = $"https://{server}";
                }
            }

            return server;
        }

        static List<Build> GetBuildConfigs(string server, string username, string password)
        {
            var buildConfigs = new List<Build>();

            using var client = new WebClient();
            if (username != null && password != null)
            {
                string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
                client.Headers[HttpRequestHeader.Authorization] = $"Basic {credentials}";
            }

            string address = $"{server}/app/rest/buildTypes";

            dynamic builds = GetJsonContent(client, address);

            foreach (JProperty propertyBuild in builds)
            {
                if (propertyBuild.First.Type == JTokenType.Array)
                {
                    foreach (dynamic build in propertyBuild.First)
                    {
                        string buildid = build.id;

                        address = $"{server}/app/rest/buildTypes/{buildid}";

                        dynamic buildConfig = GetJsonContent(client, address);

                        var isdeployment = (buildConfig.settings.property as JArray)
                            ?.Select(property => new
                            {
                                name = (string)property["name"],
                                value = (string)property["value"]
                            })
                            .Any(property =>
                                property.name?.Equals("buildConfigurationType", StringComparison.OrdinalIgnoreCase) == true
                                && property.value?.Equals("DEPLOYMENT", StringComparison.OrdinalIgnoreCase) == true)
                            ?? false;

                        if (isdeployment)
                        {
                            Log($"Skipping {buildid} since it is a deployment build.");
                            continue;
                        }

                        var buildsteps = new List<Buildstep>();

                        foreach (JProperty propertyStep in buildConfig.steps)
                        {
                            if (propertyStep.First.Type == JTokenType.Array)
                            {
                                foreach (dynamic step in propertyStep.First)
                                {
                                    buildsteps.Add(new Buildstep
                                    {
                                        Stepid = step.id,
                                        Stepname = step.name,
                                        Steptype = step.type,
                                        Disabled = step.disabled,
                                        Disable = false
                                    });
                                }
                            }
                        }

                        buildConfigs.Add(new Build
                        {
                            Buildid = buildid,
                            Steps = buildsteps,
                            Properties = new Dictionary<string, string>(),
                            DontRun = false
                        });
                    }
                }
            }

            return buildConfigs;
        }

        static void PrintBuildSteps(List<Build> builds)
        {
            int[] collengths = {
                builds.Max(b => b.Steps.Max(s => s.Stepname.Length)),
                builds.Max(b => b.Steps.Max(s => s.Steptype.Length)),
                builds.Max(b => b.Steps.Max(s => s.Stepid.Length)),
                builds.Max(b => b.Steps.Max(s => s.Disabled.HasValue && s.Disabled.Value ? "Disabled".Length : "Enabled".Length)),
                builds.Max(b => b.Steps.Max(s => s.Disable? "Disable".Length : "Enable".Length))
            };

            foreach (var build in builds)
            {
                if (build.DontRun)
                {
                    Log($"{build.Buildid}", ConsoleColor.DarkGray);
                }
                else
                {
                    Log($"{build.Buildid}");
                }
                //Log($"{build.buildid}: {string.Join(",", build.steps.Select(s => $"{s.stepname}|{s.steptype}"))}");
                foreach (Buildstep buildstep in build.Steps)
                {
                    string stepname = $"'{buildstep.Stepname}'".PadRight(collengths[0] + 2);
                    string steptype = buildstep.Steptype.ToString().PadRight(collengths[1]);
                    string stepid = buildstep.Stepid.ToString().PadRight(collengths[2]);

                    string disabled = buildstep.Disabled.HasValue && buildstep.Disabled.Value ? "Disabled" : "Enabled";
                    disabled = disabled.PadRight(collengths[3]);
                    string disable = buildstep.Disable ? "Disable" : "Enable";
                    disable = disable.PadRight(collengths[4]);

                    if ((buildstep.Disabled.HasValue && buildstep.Disabled.Value) || buildstep.Disable)
                    {
                        Log($"    {stepname} {steptype} {stepid} {disabled} {disable}", ConsoleColor.DarkGray);
                    }
                    else
                    {
                        Log($"    {stepname} {steptype} {stepid} {disabled} {disable}");
                    }
                }
            }

            var allsteps = builds.SelectMany(b => b.Steps)
                .ToArray();


            var steptypes = allsteps
                .Where(t => !t.Disabled.HasValue || !t.Disabled.Value)
                .GroupBy(s => s.Steptype)
                .ToArray();

            Log($"Found {steptypes.Length} enabled build step types.");

            foreach (var steptype in steptypes.OrderBy(t => -t.Count()))
            {
                List<string> refs = builds
                    .Where(b => b.Steps.Any(s => s.Steptype == steptype.Key && (!s.Disabled.HasValue || !s.Disabled.Value)))
                    .SelectMany(b => b.Steps
                        .Where(s => s.Steptype == steptype.Key && (!s.Disabled.HasValue || !s.Disabled.Value))
                        .Select(s => $"'{b.Buildid}.{s.Stepname}'"))
                    .Take(3)
                    .ToList();
                if (refs.Count == 3)
                {
                    refs[2] = "...";
                }
                Log($" {steptype.Key}: {steptype.Count()}: {string.Join(", ", refs)}");
            }
        }

        static bool GetBooleanEnvironmentVariable(string variableName, bool defaultValue)
        {
            bool returnValue;

            string stringValue = Environment.GetEnvironmentVariable(variableName);
            if (stringValue == null)
            {
                returnValue = defaultValue;
                Log($"Environment variable not specified: '{variableName}', using: '{returnValue}'");
            }
            else
            {
                if (bool.TryParse(stringValue, out bool boolValue))
                {
                    returnValue = boolValue;
                    Log($"Got environment variable: '{variableName}', value: '{returnValue}'");
                }
                else
                {
                    returnValue = defaultValue;
                    Log($"Got malformed environment variable: '{variableName}', using: '{returnValue}'");
                }
            }

            return returnValue;
        }

        static string[] GetStringArrayEnvironmentVariable(string variableName, string[] defaultValues)
        {
            string[] returnValues;

            string stringValue = Environment.GetEnvironmentVariable(variableName);
            if (stringValue == null)
            {
                returnValues = defaultValues;
                if (returnValues == null)
                {
                    Log($"Environment variable not specified: '{variableName}', using: <null>");
                }
                else
                {
                    Log($"Environment variable not specified: '{variableName}', using: '{string.Join("', '", returnValues)}'");
                }
            }
            else
            {
                returnValues = stringValue.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Log($"Got environment variable: '{variableName}', values: '{string.Join("', '", returnValues)}'");
            }

            return returnValues;
        }

        static Dictionary<string, string> GetDictionaryEnvironmentVariable(string variableName, Dictionary<string, string> defaultValues)
        {
            Dictionary<string, string> returnValues;

            string stringValue = Environment.GetEnvironmentVariable(variableName);
            if (stringValue == null)
            {
                returnValues = defaultValues;
                if (returnValues == null)
                {
                    Log($"Environment variable not specified: '{variableName}', using: <null>");
                }
                else
                {
                    Log($"Environment variable not specified: '{variableName}', using: {string.Join(", ", returnValues.Select(v => $"{v.Key}='{v.Value}'"))}");
                }
            }
            else
            {
                string[] values = stringValue.Split(',');

                foreach (string v in values.Where(v => !v.Contains('=')))
                {
                    LogTCWarning($"Ignoring malformed environment variable ({variableName}): {v}");
                }

                values = values.Where(v => v.Contains('=')).ToArray();

                returnValues = values.ToDictionary(v => v.Split('=')[0], v => v.Split('=')[1]);
                Log($"Got environment variable: '{variableName}', value: '{string.Join(", ", returnValues.Select(v => $"{v.Key}='{v.Value}'"))}'");
            }

            return returnValues;
        }

        static void TriggerBuilds(string server, string username, string password, List<Build> builds, bool dryRun)
        {
            var buildnames = new List<string>();

            using var client = new WebClient();
            if (username != null && password != null)
            {
                string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
                client.Headers[HttpRequestHeader.Authorization] = $"Basic {credentials}";
            }

            foreach (Build build in builds)
            {
                LogTCSection($"Queuing: {build.Buildid}", () =>
                {
                    foreach (Buildstep step in build.Steps.Where(s => s.Disable))
                    {
                        string stepAddress = $"{server}/app/rest/buildTypes/{build.Buildid}/steps/{step.Stepid}/disabled";
                        Log($"Disabling: {build.Buildid}.{step.Stepid}: '{step.Stepname}'", ConsoleColor.DarkMagenta);
                        PutPlainTextContent(client, stepAddress, "true", build.DontRun || dryRun);
                    }

                    string propertiesstring = string.Empty;
                    if (build.Properties.Count() > 0)
                    {
                        propertiesstring = string.Join(string.Empty,
                            build.Properties.Select(p => $"<property name='{p.Key}' value='{p.Value}'/>"));

                        propertiesstring = $"<properties>{propertiesstring}</properties>";
                    }

                    string buildContent = $"<build><buildType id='{build.Buildid}'/>{propertiesstring}</build>";
                    string buildAddress = $"{server}/app/rest/buildQueue";
                    Log($"Triggering build: {build.Buildid}", ConsoleColor.Magenta);
                    dynamic queueResult = PostXmlContent(client, buildAddress, buildContent, build.DontRun || dryRun);

                    if (!(build.DontRun || dryRun))
                    {
                        bool added = false;
                        do
                        {
                            Thread.Sleep(1000);
                            string buildid = queueResult.id;
                            string queueAddress = $"{server}/app/rest/builds/id:{buildid}";
                            dynamic buildResult = GetJsonContent(client, queueAddress);
                            if (buildResult.waitReason == null)
                            {
                                Log($"Build {buildid} queued.", ConsoleColor.Green);
                                added = true;
                            }
                            else if (buildResult.waitReason.Value.EndsWith("Build settings have not been finalized"))
                            {
                                Log($"Build {buildid} not queued yet: {buildResult.waitReason.Value}");
                            }
                            else
                            {
                                Log($"Broken build {buildid} is broken, cannot be bothered: {buildResult.waitReason.Value}", ConsoleColor.Yellow);
                                added = true;
                            }
                        }
                        while (!added);
                    }

                    foreach (Buildstep step in build.Steps.Where(s => s.Disable))
                    {
                        string stepAddress = $"{server}/app/rest/buildTypes/{build.Buildid}/steps/{step.Stepid}/disabled";
                        Log($"Enabling: {build.Buildid}.{step.Stepid}: '{step.Stepname}'", ConsoleColor.DarkMagenta);
                        PutPlainTextContent(client, stepAddress, "false", build.DontRun || dryRun);
                    }
                });
            }
        }

        static string PutPlainTextContent(WebClient client, string address, string content, bool dryRun)
        {
            client.Headers["Content-Type"] = "text/plain";
            client.Headers["Accept"] = "text/plain";
            Log($"Address: '{address}', content: '{content}'" + (dryRun ? $", dryRun: {dryRun}" : string.Empty));
            try
            {
                if (_buildDebug)
                {
                    string debugfile = $"BuildDebug_{RestDebugCount++}.txt";
                    if (!_writtenLogs.ContainsKey(debugfile))
                    {
                        File.WriteAllText(debugfile, content);
                        _writtenLogs[debugfile] = true;
                    }
                }
                string result = null;
                if (!dryRun)
                {
                    result = client.UploadString(address, "PUT", content);
                }
                if (_buildDebug)
                {
                    string resultfile = $"BuildDebug_{RestDebugCount++}.result.txt";
                    if (!_writtenLogs.ContainsKey(resultfile))
                    {
                        if (result == null)
                        {
                            File.WriteAllText(resultfile, "N/A because of DryRun");
                        }
                        else
                        {
                            File.WriteAllText(resultfile, result);
                        }
                        _writtenLogs[resultfile] = true;
                    }
                }
                return result;
            }
            catch (WebException ex)
            {
                throw new ApplicationException(ex.Message, ex);
            }
        }

        static JObject GetJsonContent(WebClient client, string address)
        {
            client.Headers["Accept"] = "application/json";
            Log($"Address: '{address}'");
            try
            {
                string result = client.DownloadString(address);
                if (_buildDebug)
                {
                    string resultfile = $"BuildDebug_{RestDebugCount++}.result.json";
                    if (!_writtenLogs.ContainsKey(resultfile))
                    {
                        string pretty = JToken.Parse(result).ToString(Formatting.Indented);
                        File.WriteAllText(resultfile, pretty);
                    }
                    _writtenLogs[resultfile] = true;
                }
                JObject jobject = JObject.Parse(result);
                return jobject;
            }
            catch (WebException ex)
            {
                throw new ApplicationException(ex.Message, ex);
            }
        }

        static JObject PutJsonContent(WebClient client, string address, JObject content, bool dryRun)
        {
            client.Headers["Content-Type"] = "application/json";
            client.Headers["Accept"] = "application/json";
            Log($"Address: '{address}', content: '{content}'" + (dryRun ? $", dryRun: {dryRun}" : string.Empty));
            try
            {
                if (_buildDebug)
                {
                    string debugfile = $"BuildDebug_{RestDebugCount++}.json";
                    if (!_writtenLogs.ContainsKey(debugfile))
                    {
                        string pretty = content.ToString(Formatting.Indented);
                        File.WriteAllText(debugfile, pretty);
                        _writtenLogs[debugfile] = true;
                    }
                }
                string result = null;
                if (!dryRun)
                {
                    result = client.UploadString(address, "PUT", content.ToString());
                }
                if (_buildDebug)
                {
                    string resultfile = $"BuildDebug_{RestDebugCount++}.result.json";
                    if (!_writtenLogs.ContainsKey(resultfile))
                    {
                        if (result == null)
                        {
                            File.WriteAllText(resultfile, "N/A because of DryRun");
                        }
                        else
                        {
                            string pretty = JObject.Parse(result).ToString(Formatting.Indented);
                            File.WriteAllText(resultfile, pretty);
                        }
                        _writtenLogs[resultfile] = true;
                    }
                }
                if (result == null)
                {
                    return null;
                }
                else
                {
                    JObject jobject = JObject.Parse(result);
                    return jobject;
                }
            }
            catch (WebException ex)
            {
                throw new ApplicationException(ex.Message, ex);
            }
        }

        static JObject PostXmlContent(WebClient client, string address, string content, bool dryRun)
        {
            client.Headers["Content-Type"] = "application/xml";
            client.Headers["Accept"] = "application/json";
            Log($"Address: '{address}', content: '{content}'" + (dryRun ? $", dryRun: {dryRun}" : string.Empty));
            try
            {
                if (_buildDebug)
                {
                    string debugfile = $"BuildDebug_{RestDebugCount++}.xml";
                    if (!_writtenLogs.ContainsKey(debugfile))
                    {
                        File.WriteAllText(debugfile, content);
                        _writtenLogs[debugfile] = true;
                    }
                }
                string result = null;
                if (!dryRun)
                {
                    result = client.UploadString(address, content);
                }
                if (_buildDebug)
                {
                    string resultfile = $"BuildDebug_{RestDebugCount++}.result.json";
                    if (!_writtenLogs.ContainsKey(resultfile))
                    {
                        if (result == null)
                        {
                            File.WriteAllText(resultfile, "N/A because of DryRun");
                        }
                        else
                        {
                            string pretty = JObject.Parse(result).ToString(Formatting.Indented);
                            File.WriteAllText(resultfile, pretty);
                        }
                        _writtenLogs[resultfile] = true;
                    }
                }
                if (result == null)
                {
                    return null;
                }
                else
                {
                    JObject jobject = JObject.Parse(result);
                    return jobject;
                }
            }
            catch (WebException ex)
            {
                throw new ApplicationException(ex.Message, ex);
            }
        }

        static void GetCredentials(out string username, out string password)
        {
            username = Environment.GetEnvironmentVariable("BuildUsername");
            password = Environment.GetEnvironmentVariable("BuildPassword");

            if (username != null)
            {
                Log("Got username from environment variable.");
            }
            if (password != null)
            {
                Log("Got password from environment variable.");
            }

            if (username == null || password == null)
            {
                var tcvariables = GetTeamcityBuildVariables();

                if (username == null && tcvariables.ContainsKey("teamcity.auth.userId"))
                {
                    username = tcvariables["teamcity.auth.userId"];
                    Log("Got username from Teamcity.");
                }
                if (password == null && tcvariables.ContainsKey("teamcity.auth.password"))
                {
                    password = tcvariables["teamcity.auth.password"];
                    Log("Got password from Teamcity.");
                }
            }

            if (username == null)
            {
                Log("No username specified.");
            }
            if (password == null)
            {
                Log("No password specified.");
            }
        }

        static Dictionary<string, string> GetTeamcityBuildVariables()
        {
            string buildpropfile = Environment.GetEnvironmentVariable("TEAMCITY_BUILD_PROPERTIES_FILE");
            if (string.IsNullOrEmpty(buildpropfile))
            {
                Log("Couldn't find Teamcity build properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(buildpropfile))
            {
                Log($"Couldn't find Teamcity build properties file: '{buildpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity build properties file: '{buildpropfile}'");
            var valuesBuild = GetPropValues(buildpropfile);

            if (_buildDebug)
            {
                LogTCSection("Teamcity Properties", valuesBuild.Select(p => $"Build: {p.Key}={p.Value}"));
            }

            return valuesBuild;
        }

        static Dictionary<string, string> GetTeamcityConfigVariables()
        {
            string buildpropfile = Environment.GetEnvironmentVariable("TEAMCITY_BUILD_PROPERTIES_FILE");
            if (string.IsNullOrEmpty(buildpropfile))
            {
                Log("Couldn't find Teamcity build properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(buildpropfile))
            {
                Log($"Couldn't find Teamcity build properties file: '{buildpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity build properties file: '{buildpropfile}'");
            var valuesBuild = GetPropValues(buildpropfile);

            if (_buildDebug)
            {
                LogTCSection("Teamcity Properties", valuesBuild.Select(p => $"Build: {p.Key}={p.Value}"));
            }

            string configpropfile = valuesBuild["teamcity.configuration.properties.file"];
            if (string.IsNullOrEmpty(configpropfile))
            {
                Log("Couldn't find Teamcity config properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(configpropfile))
            {
                Log($"Couldn't find Teamcity config properties file: '{configpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity config properties file: '{configpropfile}'");
            var valuesConfig = GetPropValues(configpropfile);

            if (_buildDebug)
            {
                LogTCSection("Teamcity Properties", valuesConfig.Select(p => $"Config: {p.Key}={p.Value}"));
            }

            return valuesConfig;
        }

        static Dictionary<string, string> GetPropValues(string filename)
        {
            string[] rows = File.ReadAllLines(filename);

            var dic = new Dictionary<string, string>();

            foreach (string row in rows)
            {
                int index = row.IndexOf('=');
                if (index != -1)
                {
                    string key = row.Substring(0, index);
                    string value = Regex.Unescape(row.Substring(index + 1));
                    dic[key] = value;
                }
            }

            return dic;
        }

        private static void LogTCSection(string message, IEnumerable<string> collection)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"##teamcity[blockOpened name='{message}']");
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
            try
            {
                Console.WriteLine(string.Join(string.Empty, collection.Select(t => $"{t}{Environment.NewLine}")));
            }
            finally
            {
                oldColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"##teamcity[blockClosed name='{message}']");
                }
                finally
                {
                    Console.ForegroundColor = oldColor;
                }
            }
        }

        private static void LogTCSection(string message, Action action)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"##teamcity[blockOpened name='{message}']");
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
            try
            {
                action.Invoke();
            }
            finally
            {
                oldColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"##teamcity[blockClosed name='{message}']");
                }
                finally
                {
                    Console.ForegroundColor = oldColor;
                }
            }
        }

        private static T LogTCSection<T>(string message, Func<T> func)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"##teamcity[blockOpened name='{message}']");
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
            T result;
            try
            {
                result = func.Invoke();
            }
            finally
            {
                oldColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"##teamcity[blockClosed name='{message}']");
                }
                finally
                {
                    Console.ForegroundColor = oldColor;
                }
            }

            return result;
        }

        private static void LogTCError(string message)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"##teamcity[message text='{message}' status='ERROR']");
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
        }

        private static void LogTCWarning(string message)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"##teamcity[message text='{message}' status='WARNING']");
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
        }

        private static void Log(string message, ConsoleColor color)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Log(message);
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
        }

        private static void Log(string message)
        {
            string hostname = Dns.GetHostName();
            Console.WriteLine($"{hostname}: {message}");
        }
    }
}
