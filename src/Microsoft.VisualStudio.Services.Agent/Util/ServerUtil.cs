// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace Microsoft.VisualStudio.Services.Agent.Util
{
    public class ServerUtil
    {
        private DeploymentFlags _deploymentType;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope", MessageId = "locationServer")]
        private async Task<Location.ConnectionData> GetConnectionData(string serverUrl, VssCredentials credentials, ILocationServer locationServer)
        {
            VssConnection connection = VssUtil.CreateConnection(new Uri(serverUrl), credentials);
            await locationServer.ConnectAsync(connection);
            return await locationServer.GetConnectionDataAsync();
        }

        /// <summary>
        /// Returns true if server deployment type is Hosted.
        /// An exception will be thrown if the type was not determined before.
        /// </summary>
        public bool IsDeploymentTypeHostedIfDetermined()
        {
            switch (_deploymentType)
            {
                case DeploymentFlags.Hosted:
                    return true;
                case DeploymentFlags.OnPremises:
                    return false;
                case DeploymentFlags.None:
                    throw new Exception($"Deployment type has not been determined");
                default:
                    throw new Exception($"Unable to recognize deployment type: '{_deploymentType}'");
            }
        }

        /// <summary>
        /// Returns true if server deployment type is Hosted.
        /// Determines the type if it has not been determined yet.
        /// </summary>
        public async Task<bool> IsDeploymentTypeHosted(string serverUrl, VssCredentials credentials, ILocationServer locationServer)
        {
            // Check if deployment type has not been determined yet
            if (_deploymentType == DeploymentFlags.None)
            {
                // Determine the service deployment type based on connection data. (Hosted/OnPremises)
                var connectionData = await GetConnectionData(serverUrl, credentials, locationServer);
                _deploymentType = connectionData.DeploymentType;
            }

            return IsDeploymentTypeHostedIfDetermined();
        }
    }
}
