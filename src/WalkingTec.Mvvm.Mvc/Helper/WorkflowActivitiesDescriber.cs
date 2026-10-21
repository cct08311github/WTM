using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elsa.Metadata;
using Elsa.Server.Api.Helpers;
using Elsa.Server.Api.Services;
using Elsa.Services;
using Elsa.Services.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Builds the activity list of the workflow designer in-process (#216), the same list Elsa's own <c>GET v{version}/activities</c>
    /// endpoint returns, so <see cref="WtmMiddleware"/> does not have to call that endpoint over HTTP (which needed a secret header
    /// and a call target that could follow the Host header).
    /// The logic is the one of Elsa.Server.Api 2.17.0 <c>Elsa.Server.Api.Endpoints.Activities.List</c>:
    /// all activity types from <see cref="IActivityTypeService"/>, each described, with non-browsable input / output properties removed,
    /// serialized with the endpoint serializer settings of <see cref="IEndpointContentSerializerSettingsProvider"/>
    /// (Newtonsoft.Json, camelCase, NodaTime, flag enums, nulls ignored).
    /// </summary>
    public static class WorkflowActivitiesDescriber
    {
        /// <summary>
        /// Returns the JSON text of the activity list, or null when the workflow engine is not registered (EnableWorkflow is off).
        /// </summary>
        public static async Task<string> DescribeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            var activityTypeService = services?.GetService<IActivityTypeService>();
            if (activityTypeService == null)
            {
                return null;
            }
            var settings = services.GetService<IEndpointContentSerializerSettingsProvider>()?.GetSettings()
                ?? SerializationHelper.GetSettingsForEndpoint();

            var activityTypes = await activityTypeService.GetActivityTypesAsync(cancellationToken).ConfigureAwait(false);
            var descriptors = await Task.WhenAll(activityTypes.Select(x => DescribeActivityAsync(activityTypeService, x, cancellationToken)).ToList()).ConfigureAwait(false);
            return JsonConvert.SerializeObject(descriptors, settings);
        }

        private static async Task<ActivityDescriptor> DescribeActivityAsync(IActivityTypeService activityTypeService, ActivityType activityType, CancellationToken cancellationToken)
        {
            var descriptor = await activityTypeService.DescribeActivityType(activityType, cancellationToken).ConfigureAwait(false);
            descriptor.InputProperties = descriptor.InputProperties.Where(x => x.IsBrowsable ?? true).ToArray();
            descriptor.OutputProperties = descriptor.OutputProperties.Where(x => x.IsBrowsable ?? true).ToArray();
            return descriptor;
        }
    }
}
