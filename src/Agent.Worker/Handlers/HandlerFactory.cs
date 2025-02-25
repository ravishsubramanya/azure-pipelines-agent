// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    [ServiceLocator(Default = typeof(HandlerFactory))]
    public interface IHandlerFactory : IAgentService
    {
        IHandler Create(
            IExecutionContext executionContext,
            Pipelines.TaskStepDefinitionReference task,
            IStepHost stepHost,
            List<ServiceEndpoint> endpoints,
            List<SecureFile> secureFiles,
            HandlerData data,
            Dictionary<string, string> inputs,
            Dictionary<string, string> environment,
            Variables runtimeVariables,
            string taskDirectory);
    }

    public sealed class HandlerFactory : AgentService, IHandlerFactory
    {
        public IHandler Create(
            IExecutionContext executionContext,
            Pipelines.TaskStepDefinitionReference task,
            IStepHost stepHost,
            List<ServiceEndpoint> endpoints,
            List<SecureFile> secureFiles,
            HandlerData data,
            Dictionary<string, string> inputs,
            Dictionary<string, string> environment,
            Variables runtimeVariables,
            string taskDirectory)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(stepHost, nameof(stepHost));
            ArgUtil.NotNull(endpoints, nameof(endpoints));
            ArgUtil.NotNull(secureFiles, nameof(secureFiles));
            ArgUtil.NotNull(data, nameof(data));
            ArgUtil.NotNull(inputs, nameof(inputs));
            ArgUtil.NotNull(environment, nameof(environment));
            ArgUtil.NotNull(runtimeVariables, nameof(runtimeVariables));
            ArgUtil.NotNull(taskDirectory, nameof(taskDirectory));

            // Create the handler.
            IHandler handler;
            if (data is BaseNodeHandlerData)
            {
                // Node 6
                if (data is NodeHandlerData) {       
                    executionContext.Warning(StringUtil.Loc("DeprecatedNode6"));
                }
                // Node 6 and 10.
                handler = HostContext.CreateService<INodeHandler>();
                (handler as INodeHandler).Data = data as BaseNodeHandlerData;
            }
            else if (data is PowerShell3HandlerData)
            {
                // PowerShell3.
                handler = HostContext.CreateService<IPowerShell3Handler>();
                (handler as IPowerShell3Handler).Data = data as PowerShell3HandlerData;
            }
            else if (data is PowerShellExeHandlerData)
            {
                // PowerShellExe.
                handler = HostContext.CreateService<IPowerShellExeHandler>();
                (handler as IPowerShellExeHandler).Data = data as PowerShellExeHandlerData;
            }
            else if (data is ProcessHandlerData)
            {
                // Process.
                handler = HostContext.CreateService<IProcessHandler>();
                (handler as IProcessHandler).Data = data as ProcessHandlerData;
            }
            else if (data is PowerShellHandlerData)
            {
                // PowerShell.
                handler = HostContext.CreateService<IPowerShellHandler>();
                (handler as IPowerShellHandler).Data = data as PowerShellHandlerData;
            }
            else if (data is AzurePowerShellHandlerData)
            {
                // AzurePowerShell.
                handler = HostContext.CreateService<IAzurePowerShellHandler>();
                (handler as IAzurePowerShellHandler).Data = data as AzurePowerShellHandlerData;
            }
            else if (data is AgentPluginHandlerData)
            {
                // Agent plugin
                handler = HostContext.CreateService<IAgentPluginHandler>();
                (handler as IAgentPluginHandler).Data = data as AgentPluginHandlerData;
            }
            else
            {
                // This should never happen.
                throw new NotSupportedException();
            }

            handler.Endpoints = endpoints;
            handler.Task = task;
            handler.Environment = environment;
            handler.RuntimeVariables = runtimeVariables;
            handler.ExecutionContext = executionContext;
            handler.StepHost = stepHost;
            handler.Inputs = inputs;
            handler.SecureFiles = secureFiles;
            handler.TaskDirectory = taskDirectory;
            return handler;
        }
    }
}