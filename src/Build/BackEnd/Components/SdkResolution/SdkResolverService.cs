﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Microsoft.Build.BackEnd.SdkResolution
{
    /// <summary>
    /// The main implementation of <see cref="ISdkResolverService"/> which resolves SDKs.  This class is the central location for all SDK resolution and is used
    /// directly by the main node and non-build evaluations and is used indirectly by the out-of-proc node when it sends requests to the main node.
    ///
    /// All access to this class must go through the singleton <see cref="SdkResolverService.Instance"/>.
    /// </summary>
    internal sealed class SdkResolverService : ISdkResolverService
    {
        /// <summary>
        /// Stores the singleton instance for a particular process.
        /// </summary>
        private static readonly Lazy<SdkResolverService> InstanceLazy = new Lazy<SdkResolverService>(() => new SdkResolverService(), isThreadSafe: true);

        /// <summary>
        /// A lock object used for this class.
        /// </summary>
        private readonly object _lockObject = new object();

        /// <summary>
        /// Stores resolver state by build submission ID.
        /// </summary>
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<SdkResolver, object>> _resolverStateBySubmission = new ConcurrentDictionary<int, ConcurrentDictionary<SdkResolver, object>>();

        /// <summary>
        /// Stores the list of SDK resolvers which were loaded.
        /// </summary>
        private IList<SdkResolver> _resolvers;

        /// <summary>
        /// Stores an <see cref="SdkResolverLoader"/> which can load registered SDK resolvers.
        /// </summary>
        private SdkResolverLoader _sdkResolverLoader = new SdkResolverLoader();

        private SdkResolverService()
        {
        }

        /// <summary>
        /// Gets the current instance of <see cref="SdkResolverService"/> for this process.
        /// </summary>
        public static SdkResolverService Instance => InstanceLazy.Value;

        /// <inheritdoc cref="ISdkResolverService.SendPacket"/>
        public Action<INodePacket> SendPacket { get; }

        /// <summary>
        /// Determines if the <see cref="SdkReference"/> is the same as the specified version.  If the <paramref name="sdk"/> object has <code>null</code> for the version,
        /// this method will always return true since <code>null</code> can match any version.
        /// </summary>
        /// <param name="sdk">An <see cref="SdkReference"/> object.</param>
        /// <param name="version">The version to compare.</param>
        /// <returns><code>true</code> if the specified SDK reference has the same version as the specified result, otherwise <code>false</code>.</returns>
        public static bool IsReferenceSameVersion(SdkReference sdk, string version)
        {
            // If the reference has a null version, it matches any result
            if (String.IsNullOrEmpty(sdk.Version))
            {
                return true;
            }

            return String.Equals(sdk.Version, version, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc cref="ISdkResolverService.ClearCache"/>
        public void ClearCache(int submissionId)
        {
            ConcurrentDictionary<SdkResolver, object> notused;

            _resolverStateBySubmission.TryRemove(submissionId, out notused);
        }

        /// <summary>
        /// Resolves and SDK and gets a result.
        /// </summary>
        /// <param name="submissionId">The build submission ID that the resolution request is for.</param>
        /// <param name="sdk">The <see cref="SdkReference"/> containing information about the referenced SDK.</param>
        /// <param name="loggingContext">The <see cref="LoggingContext"/> to use when logging messages during resolution.</param>
        /// <param name="sdkReferenceLocation">The <see cref="ElementLocation"/> of the element which referenced the SDK.</param>
        /// <param name="solutionPath">The full path to the solution, if any, that is being built.</param>
        /// <param name="projectPath">The full path to that referenced the SDK.</param>
        /// <returns>An <see cref="SdkResult"/> containing information of the SDK if it could be resolved, otherwise <code>null</code>.</returns>
        public SdkResult GetSdkResult(int submissionId, SdkReference sdk, LoggingContext loggingContext, ElementLocation sdkReferenceLocation, string solutionPath, string projectPath)
        {
            // Lazy initialize the SDK resolvers
            if (_resolvers == null)
            {
                Initialize(loggingContext, sdkReferenceLocation);
            }

            List<SdkResult> results = new List<SdkResult>();

            try
            {
                // Loop through resolvers which have already been sorted by priority, returning the first result that was successful
                SdkLogger buildEngineLogger = new SdkLogger(loggingContext);

                foreach (SdkResolver sdkResolver in _resolvers)
                {
                    SdkResolverContext context = new SdkResolverContext(buildEngineLogger, projectPath, solutionPath, ProjectCollection.Version)
                    {
                        State = GetResolverState(submissionId, sdkResolver)
                    };

                    SdkResultFactory resultFactory = new SdkResultFactory(sdk);
                    try
                    {
                        SdkResult result = (SdkResult) sdkResolver.Resolve(sdk, context, resultFactory);

                        SetResolverState(submissionId, sdkResolver, context.State);

                        if (result == null)
                        {
                            continue;
                        }

                        if (result.Success)
                        {
                            LogWarnings(loggingContext, sdkReferenceLocation, result);
                            return result;
                        }

                        results.Add(result);
                    }
                    catch (Exception e)
                    {
                        loggingContext.LogFatalBuildError(e, new BuildEventFileInfo(sdkReferenceLocation));
                    }
                }
            }
            catch (Exception e)
            {
                loggingContext.LogFatalBuildError(e, new BuildEventFileInfo(sdkReferenceLocation));
                throw;
            }

            foreach (SdkResult result in results)
            {
                LogWarnings(loggingContext, sdkReferenceLocation, result);

                if (result.Errors != null)
                {
                    foreach (string error in result.Errors)
                    {
                        loggingContext.LogErrorFromText(subcategoryResourceName: null, errorCode: null, helpKeyword: null, file: new BuildEventFileInfo(sdkReferenceLocation), message: error);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc cref="ISdkResolverService.ResolveSdk"/>
        public string ResolveSdk(int submissionId, SdkReference sdk, LoggingContext loggingContext, ElementLocation sdkReferenceLocation, string solutionPath, string projectPath)
        {
            SdkResult result = GetSdkResult(submissionId, sdk, loggingContext, sdkReferenceLocation, solutionPath, projectPath);

            return result?.Path;
        }

        /// <summary>
        /// Used for unit tests only.  This is currently only called through reflection in Microsoft.Build.Engine.UnitTests.TransientSdkResolution.CallResetForTests
        /// </summary>
        /// <param name="resolverLoader">An <see cref="SdkResolverLoader"/> to use for loading SDK resolvers.</param>
        /// <param name="resolvers">Explicit set of SdkResolvers to use for all SDK resolution.</param>
        internal void InitializeForTests(SdkResolverLoader resolverLoader = null, IList<SdkResolver> resolvers = null)
        {
            if (resolverLoader != null)
            {
                _sdkResolverLoader = resolverLoader;
            }

            _resolvers = resolvers;
        }

        private static void LogWarnings(LoggingContext loggingContext, ElementLocation location, SdkResult result)
        {
            if (result.Warnings == null)
            {
                return;
            }

            foreach (string warning in result.Warnings)
            {
                loggingContext.LogWarningFromText(null, null, null, new BuildEventFileInfo(location), warning);
            }
        }

        private object GetResolverState(int submissionId, SdkResolver resolver)
        {
            // Do not fetch state for resolution requests that are not associated with a valid build submission ID
            if (submissionId != BuildEventContext.InvalidSubmissionId)
            {
                ConcurrentDictionary<SdkResolver, object> resolverState;

                if (_resolverStateBySubmission.TryGetValue(submissionId, out resolverState))
                {
                    object state;

                    if (resolverState.TryGetValue(resolver, out state))
                    {
                        return state;
                    }
                }
            }

            return null;
        }

        private void Initialize(LoggingContext loggingContext, ElementLocation location)
        {
            lock (_lockObject)
            {
                if (_resolvers != null)
                {
                    return;
                }

                _resolvers = _sdkResolverLoader.LoadResolvers(loggingContext, location);
            }
        }

        private void SetResolverState(int submissionId, SdkResolver resolver, object state)
        {
            // Do not set state for resolution requests that are not associated with a valid build submission ID
            if (submissionId != BuildEventContext.InvalidSubmissionId)
            {
                ConcurrentDictionary<SdkResolver, object> resolverState = _resolverStateBySubmission.GetOrAdd(submissionId, new ConcurrentDictionary<SdkResolver, object>(Environment.ProcessorCount, _resolvers.Count));

                resolverState.AddOrUpdate(resolver, state, (sdkResolver, obj) => state);
            }
        }
    }
}