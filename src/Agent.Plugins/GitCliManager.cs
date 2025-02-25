// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Agent.Sdk;
using Agent.Sdk.Knob;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Agent.Plugins.Repository
{
    public class GitCliManager
    {
        private static Encoding _encoding
        {
            get => PlatformUtil.RunningOnWindows
                ? Encoding.UTF8
                : null;
        }

        protected readonly Dictionary<string, string> gitEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "GIT_TERMINAL_PROMPT", "0" },
        };

        protected string gitPath = null;
        protected Version gitVersion = null;
        protected string gitLfsPath = null;
        protected Version gitLfsVersion = null;

        public GitCliManager(Dictionary<string, string> envs = null)
        {
            if (envs != null)
            {
                foreach (var env in envs)
                {
                    if (!string.IsNullOrEmpty(env.Key))
                    {
                        gitEnv[env.Key] = env.Value ?? string.Empty;
                    }
                }
            }
        }

        public bool EnsureGitVersion(Version requiredVersion, bool throwOnNotMatch)
        {
            ArgUtil.NotNull(gitPath, nameof(gitPath));
            ArgUtil.NotNull(gitVersion, nameof(gitVersion));

            if (gitVersion < requiredVersion && throwOnNotMatch)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredGitVersion", requiredVersion, gitPath, gitVersion));
            }

            return gitVersion >= requiredVersion;
        }

        public bool EnsureGitLFSVersion(Version requiredVersion, bool throwOnNotMatch)
        {
            ArgUtil.NotNull(gitLfsPath, nameof(gitLfsPath));
            ArgUtil.NotNull(gitLfsVersion, nameof(gitLfsVersion));

            if (gitLfsVersion < requiredVersion && throwOnNotMatch)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredGitLfsVersion", requiredVersion, gitLfsPath, gitLfsVersion));
            }

            return gitLfsVersion >= requiredVersion;
        }

        public virtual async Task LoadGitExecutionInfo(AgentTaskPluginExecutionContext context, bool useBuiltInGit)
        {
            // There is no built-in git for OSX/Linux
            gitPath = null;

            // Resolve the location of git.
            if (useBuiltInGit && PlatformUtil.RunningOnWindows)
            {
                string agentHomeDir = context.Variables.GetValueOrDefault("agent.homedirectory")?.Value;
                ArgUtil.NotNullOrEmpty(agentHomeDir, nameof(agentHomeDir));
                gitPath = Path.Combine(agentHomeDir, "externals", "git", "cmd", $"git.exe");
                gitLfsPath = Path.Combine(agentHomeDir, "externals", "git", PlatformUtil.BuiltOnX86 ? "mingw32" : "mingw64", "bin", "git-lfs.exe");

                // Prepend the PATH.
                context.Output(StringUtil.Loc("Prepending0WithDirectoryContaining1", "Path", Path.GetFileName(gitPath)));
                // We need to prepend git-lfs path first so that we call
                // externals/git/cmd/git.exe instead of externals/git/mingw**/bin/git.exe
                context.PrependPath(Path.GetDirectoryName(gitLfsPath));
                context.PrependPath(Path.GetDirectoryName(gitPath));
                context.Debug($"PATH: '{Environment.GetEnvironmentVariable("PATH")}'");
            }
            else
            {
                gitPath = WhichUtil.Which("git", require: true, trace: context);
                gitLfsPath = WhichUtil.Which("git-lfs", require: false, trace: context);
            }

            ArgUtil.File(gitPath, nameof(gitPath));

            // Get the Git version.
            gitVersion = await GitVersion(context);
            ArgUtil.NotNull(gitVersion, nameof(gitVersion));
            context.Debug($"Detect git version: {gitVersion.ToString()}.");

            // Get the Git-LFS version if git-lfs exist in %PATH%.
            if (!string.IsNullOrEmpty(gitLfsPath))
            {
                gitLfsVersion = await GitLfsVersion(context);
                context.Debug($"Detect git-lfs version: '{gitLfsVersion?.ToString() ?? string.Empty}'.");
            }

            // required 2.0, all git operation commandline args need min git version 2.0
            Version minRequiredGitVersion = new Version(2, 0);
            EnsureGitVersion(minRequiredGitVersion, throwOnNotMatch: true);

            // suggest user upgrade to 2.17 for better git experience
            Version recommendGitVersion = new Version(2, 17);
            if (!EnsureGitVersion(recommendGitVersion, throwOnNotMatch: false))
            {
                context.Output(StringUtil.Loc("UpgradeToLatestGit", recommendGitVersion, gitVersion));
            }

            // git-lfs 2.7.1 contains a bug where it doesn't include extra header from git config
            // See https://github.com/git-lfs/git-lfs/issues/3571
            bool gitLfsSupport = StringUtil.ConvertToBoolean(context.GetInput(Pipelines.PipelineConstants.CheckoutTaskInputs.Lfs));
            Version recommendedGitLfsVersion = new Version(2, 7, 2);

            if (gitLfsSupport && gitLfsVersion == new Version(2, 7, 1))
            {
                context.Output(StringUtil.Loc("UnsupportedGitLfsVersion", gitLfsVersion, recommendedGitLfsVersion));
            }

            // Set the user agent.
            string gitHttpUserAgentEnv = $"git/{gitVersion.ToString()} (vsts-agent-git/{context.Variables.GetValueOrDefault("agent.version")?.Value ?? "unknown"})";
            context.Debug($"Set git useragent to: {gitHttpUserAgentEnv}.");
            gitEnv["GIT_HTTP_USER_AGENT"] = gitHttpUserAgentEnv;
        }

        // git init <LocalDir>
        public async Task<int> GitInit(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Init git repository at: {repositoryPath}.");
            string repoRootEscapeSpace = StringUtil.Format(@"""{0}""", repositoryPath.Replace(@"""", @"\"""));
            return await ExecuteGitCommandAsync(context, repositoryPath, "init", StringUtil.Format($"{repoRootEscapeSpace}"));
        }

        // git fetch --tags --prune --progress --no-recurse-submodules [--depth=15] origin [+refs/pull/*:refs/remote/pull/*]
        public async Task<int> GitFetch(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, int fetchDepth, List<string> refSpec, string additionalCommandLine, CancellationToken cancellationToken)
        {
            context.Debug($"Fetch git repository at: {repositoryPath} remote: {remoteName}.");
            if (refSpec != null && refSpec.Count > 0)
            {
                refSpec = refSpec.Where(r => !string.IsNullOrEmpty(r)).ToList();
            }

            // Git 2.20 changed its fetch behavior, rejecting tag updates if the --force flag is not provided
            // See https://git-scm.com/docs/git-fetch for more details
            string forceTag = string.Empty;

            if (gitVersion >= new Version(2, 20))
            {
                forceTag = "--force";
            }

            bool reducedOutput = AgentKnobs.QuietCheckout.GetValue(context).AsBoolean();
            string progress = reducedOutput ? string.Empty : "--progress";

            // insert prune-tags if knob is false to sync tags with the remote
            string pruneTags = string.Empty;
            if (EnsureGitVersion(new Version(2, 17), throwOnNotMatch: false) && !AgentKnobs.DisableFetchPruneTags.GetValue(context).AsBoolean())
            {
                pruneTags = "--prune-tags";
            }

            // If shallow fetch add --depth arg
            // If the local repository is shallowed but there is no fetch depth provide for this build,
            // add --unshallow to convert the shallow repository to a complete repository
            string depth = fetchDepth > 0 ? $"--depth={fetchDepth}" : (File.Exists(Path.Combine(repositoryPath, ".git", "shallow")) ? "--unshallow" : string.Empty );

            //define options for fetch
            string options = $"{forceTag} --tags --prune {pruneTags} {progress} --no-recurse-submodules {remoteName} {depth} {string.Join(" ", refSpec)}";
            int retryCount = 0;
            int fetchExitCode = 0;
            while (retryCount < 3)
            {
                Stopwatch watch = new Stopwatch();

                watch.Start();

                fetchExitCode = await ExecuteGitCommandAsync(context, repositoryPath, "fetch", options, additionalCommandLine, cancellationToken);

                watch.Stop();

                // Publish some fetch statistics
                context.PublishTelemetry(area: "AzurePipelinesAgent", feature: "GitFetch", properties: new Dictionary<string, string>
                {
                    { "ElapsedTimeMilliseconds", $"{watch.ElapsedMilliseconds}" },
                    { "RefSpec", string.Join(" ", refSpec) },
                    { "RemoteName", remoteName },
                    { "FetchDepth", $"{fetchDepth}" },
                    { "ExitCode", $"{fetchExitCode}" },
                    { "Options", options }
                });

                if (fetchExitCode == 0)
                {
                    break;
                }
                else
                {
                    if (++retryCount < 3)
                    {
                        var backOff = BackoffTimerHelper.GetRandomBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
                        context.Warning($"Git fetch failed with exit code {fetchExitCode}, back off {backOff.TotalSeconds} seconds before retry.");
                        await Task.Delay(backOff);
                    }
                }
            }

            return fetchExitCode;
        }

        // git lfs fetch origin [ref]
        public async Task<int> GitLFSFetch(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string refSpec, string additionalCommandLine, CancellationToken cancellationToken)
        {
            context.Debug($"Fetch LFS objects for git repository at: {repositoryPath} remote: {remoteName}.");

            // default options for git lfs fetch.
            string options = StringUtil.Format($"fetch origin {refSpec}");

            int retryCount = 0;
            int fetchExitCode = 0;
            while (retryCount < 3)
            {
                fetchExitCode = await ExecuteGitCommandAsync(context, repositoryPath, "lfs", options, additionalCommandLine, cancellationToken);
                if (fetchExitCode == 0)
                {
                    break;
                }
                else
                {
                    if (++retryCount < 3)
                    {
                        var backOff = BackoffTimerHelper.GetRandomBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
                        context.Output($"Git lfs fetch failed with exit code {fetchExitCode}, back off {backOff.TotalSeconds} seconds before retry.");
                        await Task.Delay(backOff);
                    }
                }
            }

            return fetchExitCode;
        }

        // git checkout -f --progress <commitId/branch>
        public async Task<int> GitCheckout(AgentTaskPluginExecutionContext context, string repositoryPath, string committishOrBranchSpec, CancellationToken cancellationToken)
        {
            context.Debug($"Checkout {committishOrBranchSpec}.");

            // Git 2.7 support report checkout progress to stderr during stdout/err redirect.
            string options;
            if (gitVersion >= new Version(2, 7))
            {
                options = StringUtil.Format("--progress --force {0}", committishOrBranchSpec);
            }
            else
            {
                options = StringUtil.Format("--force {0}", committishOrBranchSpec);
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "checkout", options, cancellationToken);
        }

        // git clean -ffdx
        public async Task<int> GitClean(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Delete untracked files/folders for repository at {repositoryPath}.");

            // Git 2.4 support git clean -ffdx.
            string options;
            if (gitVersion >= new Version(2, 4))
            {
                options = "-ffdx";
            }
            else
            {
                options = "-fdx";
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "clean", options);
        }

        // git reset --hard HEAD
        public async Task<int> GitReset(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Undo any changes to tracked files in the working tree for repository at {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "reset", "--hard HEAD");
        }

        // get remote set-url <origin> <url>
        public async Task<int> GitRemoteAdd(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string remoteUrl)
        {
            context.Debug($"Add git remote: {remoteName} to url: {remoteUrl} for repository under: {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "remote", StringUtil.Format($"add {remoteName} {remoteUrl}"));
        }

        // get remote set-url <origin> <url>
        public async Task<int> GitRemoteSetUrl(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string remoteUrl)
        {
            context.Debug($"Set git fetch url to: {remoteUrl} for remote: {remoteName}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "remote", StringUtil.Format($"set-url {remoteName} {remoteUrl}"));
        }

        // get remote set-url --push <origin> <url>
        public async Task<int> GitRemoteSetPushUrl(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string remoteUrl)
        {
            context.Debug($"Set git push url to: {remoteUrl} for remote: {remoteName}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "remote", StringUtil.Format($"set-url --push {remoteName} {remoteUrl}"));
        }

        // git submodule foreach --recursive "git clean -ffdx"
        public async Task<int> GitSubmoduleClean(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Delete untracked files/folders for submodules at {repositoryPath}.");

            // Git 2.4 support git clean -ffdx.
            string options;
            if (gitVersion >= new Version(2, 4))
            {
                options = "-ffdx";
            }
            else
            {
                options = "-fdx";
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", $"foreach --recursive \"git clean {options}\"");
        }

        // git submodule foreach --recursive "git reset --hard HEAD"
        public async Task<int> GitSubmoduleReset(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Undo any changes to tracked files in the working tree for submodules at {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", "foreach --recursive \"git reset --hard HEAD\"");
        }

        // git submodule update --init --force [--depth=15] [--recursive]
        public async Task<int> GitSubmoduleUpdate(AgentTaskPluginExecutionContext context, string repositoryPath, int fetchDepth, string additionalCommandLine, bool recursive, CancellationToken cancellationToken)
        {
            context.Debug("Update the registered git submodules.");
            string options = "update --init --force";
            if (fetchDepth > 0)
            {
                options = options + $" --depth={fetchDepth}";
            }
            if (recursive)
            {
                options = options + " --recursive";
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", options, additionalCommandLine, cancellationToken);
        }

        // git submodule sync [--recursive]
        public async Task<int> GitSubmoduleSync(AgentTaskPluginExecutionContext context, string repositoryPath, bool recursive, CancellationToken cancellationToken)
        {
            context.Debug("Synchronizes submodules' remote URL configuration setting.");
            string options = "sync";
            if (recursive)
            {
                options = options + " --recursive";
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", options, cancellationToken);
        }

        // git config --get remote.origin.url
        public async Task<Uri> GitGetFetchUrl(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Inspect remote.origin.url for repository under {repositoryPath}");
            Uri fetchUrl = null;

            List<string> outputStrings = new List<string>();
            int exitCode = await ExecuteGitCommandAsync(context, repositoryPath, "config", "--get remote.origin.url", outputStrings);

            if (exitCode != 0)
            {
                context.Warning($"'git config --get remote.origin.url' failed with exit code: {exitCode}, output: '{string.Join(Environment.NewLine, outputStrings)}'");
            }
            else
            {
                // remove empty strings
                outputStrings = outputStrings.Where(o => !string.IsNullOrEmpty(o)).ToList();
                if (outputStrings.Count == 1 && !string.IsNullOrEmpty(outputStrings.First()))
                {
                    string remoteFetchUrl = outputStrings.First();
                    if (Uri.IsWellFormedUriString(remoteFetchUrl, UriKind.Absolute))
                    {
                        context.Debug($"Get remote origin fetch url from git config: {remoteFetchUrl}");
                        fetchUrl = new Uri(remoteFetchUrl);
                    }
                    else
                    {
                        context.Debug($"The Origin fetch url from git config: {remoteFetchUrl} is not a absolute well formed url.");
                    }
                }
                else
                {
                    context.Debug($"Unable capture git remote fetch uri from 'git config --get remote.origin.url' command's output, the command's output is not expected: {string.Join(Environment.NewLine, outputStrings)}.");
                }
            }

            return fetchUrl;
        }

        // git config <key> <value>
        public async Task<int> GitConfig(AgentTaskPluginExecutionContext context, string repositoryPath, string configKey, string configValue)
        {
            context.Debug($"Set git config {configKey} {configValue}");
            return await ExecuteGitCommandAsync(context, repositoryPath, "config", StringUtil.Format($"{configKey} {configValue}"));
        }

        // git config --get-all <key>
        public async Task<bool> GitConfigExist(AgentTaskPluginExecutionContext context, string repositoryPath, string configKey)
        {
            // git config --get-all {configKey} will return 0 and print the value if the config exist.
            context.Debug($"Checking git config {configKey} exist or not");

            // ignore any outputs by redirect them into a string list, since the output might contains secrets.
            List<string> outputStrings = new List<string>();
            int exitcode = await ExecuteGitCommandAsync(context, repositoryPath, "config", StringUtil.Format($"--get-all {configKey}"), outputStrings);

            return exitcode == 0;
        }

        // git config --unset-all <key>
        public async Task<int> GitConfigUnset(AgentTaskPluginExecutionContext context, string repositoryPath, string configKey)
        {
            context.Debug($"Unset git config --unset-all {configKey}");
            return await ExecuteGitCommandAsync(context, repositoryPath, "config", StringUtil.Format($"--unset-all {configKey}"));
        }

        // git config gc.auto 0
        public async Task<int> GitDisableAutoGC(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Disable git auto garbage collection.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "config", "gc.auto 0");
        }

        // git repack -adfl
        public async Task<int> GitRepack(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Compress .git directory.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "repack", "-adfl");
        }

        // git prune
        public async Task<int> GitPrune(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Delete unreachable objects under .git directory.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "prune", "-v");
        }

        // git lfs prune
        public async Task<int> GitLFSPrune(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            ArgUtil.NotNull(context, nameof(context));

            context.Debug("Deletes local copies of LFS files which are old, thus freeing up disk space. Prune operates by enumerating all the locally stored objects, and then deleting any which are not referenced by at least ONE of the following:");
            return await ExecuteGitCommandAsync(context, repositoryPath, "lfs", "prune");
        }

        // git count-objects -v -H
        public async Task<int> GitCountObjects(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Inspect .git directory.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "count-objects", "-v -H");
        }

        // git lfs install --local
        public async Task<int> GitLFSInstall(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Ensure git-lfs installed.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "lfs", "install --local");
        }

        // git lfs logs last
        public async Task<int> GitLFSLogs(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Get git-lfs logs.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "lfs", "logs last");
        }

        // git version
        public virtual async Task<Version> GitVersion(AgentTaskPluginExecutionContext context)
        {
            context.Debug("Get git version.");
            string workingDir = context.Variables.GetValueOrDefault("agent.workfolder")?.Value;
            ArgUtil.Directory(workingDir, "agent.workfolder");
            Version version = null;
            List<string> outputStrings = new List<string>();
            int exitCode = await ExecuteGitCommandAsync(context, workingDir, "version", null, outputStrings);
            context.Output($"{string.Join(Environment.NewLine, outputStrings)}");
            if (exitCode == 0)
            {
                // remove any empty line.
                outputStrings = outputStrings.Where(o => !string.IsNullOrEmpty(o)).ToList();
                if (outputStrings.Count == 1 && !string.IsNullOrEmpty(outputStrings.First()))
                {
                    string verString = outputStrings.First();
                    // we interested about major.minor.patch version
                    Regex verRegex = new Regex("\\d+\\.\\d+(\\.\\d+)?", RegexOptions.IgnoreCase);
                    var matchResult = verRegex.Match(verString);
                    if (matchResult.Success && !string.IsNullOrEmpty(matchResult.Value))
                    {
                        if (!Version.TryParse(matchResult.Value, out version))
                        {
                            version = null;
                        }
                    }
                }
            }

            return version;
        }

        // git lfs version
        public virtual async Task<Version> GitLfsVersion(AgentTaskPluginExecutionContext context)
        {
            context.Debug("Get git-lfs version.");
            string workingDir = context.Variables.GetValueOrDefault("agent.workfolder")?.Value;
            ArgUtil.Directory(workingDir, "agent.workfolder");
            Version version = null;
            List<string> outputStrings = new List<string>();
            int exitCode = await ExecuteGitCommandAsync(context, workingDir, "lfs version", null, outputStrings);
            context.Output($"{string.Join(Environment.NewLine, outputStrings)}");
            if (exitCode == 0)
            {
                // remove any empty line.
                outputStrings = outputStrings.Where(o => !string.IsNullOrEmpty(o)).ToList();
                if (outputStrings.Count == 1 && !string.IsNullOrEmpty(outputStrings.First()))
                {
                    string verString = outputStrings.First();
                    // we interested about major.minor.patch version
                    Regex verRegex = new Regex("\\d+\\.\\d+(\\.\\d+)?", RegexOptions.IgnoreCase);
                    var matchResult = verRegex.Match(verString);
                    if (matchResult.Success && !string.IsNullOrEmpty(matchResult.Value))
                    {
                        if (!Version.TryParse(matchResult.Value, out version))
                        {
                            version = null;
                        }
                    }
                }
            }

            return version;
        }

        protected virtual async Task<int> ExecuteGitCommandAsync(AgentTaskPluginExecutionContext context, string repoRoot, string command, string options, CancellationToken cancellationToken = default(CancellationToken))
        {
            string arg = StringUtil.Format($"{command} {options}").Trim();
            context.Command($"git {arg}");

            using (var processInvoker = new ProcessInvoker(context, disableWorkerCommands: true))
            {
                processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                {
                    context.Output(message.Data);
                };

                processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                {
                    context.Output(message.Data);
                };

                return await processInvoker.ExecuteAsync(
                    workingDirectory: repoRoot,
                    fileName: gitPath,
                    arguments: arg,
                    environment: gitEnv,
                    requireExitCodeZero: false,
                    outputEncoding: _encoding,
                    cancellationToken: cancellationToken);
            }
        }

        protected virtual async Task<int> ExecuteGitCommandAsync(AgentTaskPluginExecutionContext context, string repoRoot, string command, string options, IList<string> output)
        {
            string arg = StringUtil.Format($"{command} {options}").Trim();
            context.Command($"git {arg}");

            if (output == null)
            {
                output = new List<string>();
            }

            using (var processInvoker = new ProcessInvoker(context, disableWorkerCommands: true))
            {
                processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                {
                    output.Add(message.Data);
                };

                processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                {
                    context.Output(message.Data);
                };

                return await processInvoker.ExecuteAsync(
                    workingDirectory: repoRoot,
                    fileName: gitPath,
                    arguments: arg,
                    environment: gitEnv,
                    requireExitCodeZero: false,
                    outputEncoding: _encoding,
                    cancellationToken: default(CancellationToken));
            }
        }

        protected virtual async Task<int> ExecuteGitCommandAsync(AgentTaskPluginExecutionContext context, string repoRoot, string command, string options, string additionalCommandLine, CancellationToken cancellationToken)
        {
            string arg = StringUtil.Format($"{additionalCommandLine} {command} {options}").Trim();
            context.Command($"git {arg}");

            using (var processInvoker = new ProcessInvoker(context, disableWorkerCommands: true))
            {
                processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                {
                    context.Output(message.Data);
                };

                processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                {
                    context.Output(message.Data);
                };

                return await processInvoker.ExecuteAsync(
                    workingDirectory: repoRoot,
                    fileName: gitPath,
                    arguments: arg,
                    environment: gitEnv,
                    requireExitCodeZero: false,
                    outputEncoding: _encoding,
                    cancellationToken: cancellationToken);
            }
        }
    }
}