﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Synapse.Common.Configurations;
using Microsoft.Health.Fhir.Synapse.Common.Fhir;
using Microsoft.Health.Fhir.Synapse.Common.Models.Jobs;
using Microsoft.Health.Fhir.Synapse.Common.Models.Tasks;
using Microsoft.Health.Fhir.Synapse.Scheduler.Exceptions;
using Microsoft.Health.Fhir.Synapse.Scheduler.Tasks;

namespace Microsoft.Health.Fhir.Synapse.Scheduler.Jobs
{
    public class JobManager : IDisposable, IAsyncDisposable
    {
        private readonly IJobStore _jobStore;
        private readonly ITaskExecutor _taskExecutor;
        private readonly IFhirSpecificationProvider _fhirSpecificationProvider;
        private readonly JobSchedulerConfiguration _schedulerConfiguration;
        private readonly JobConfiguration _jobConfiguration;
        private readonly ILogger<JobManager> _logger;

        // Lock to ensure resource progresses in a job are consistent.
        private readonly object _updateJobLock = new object();

        public JobManager(
            IJobStore jobStore,
            ITaskExecutor taskExecutor,
            IFhirSpecificationProvider fhirSpecificationProvider,
            IOptions<JobSchedulerConfiguration> schedulerConfiguration,
            IOptions<JobConfiguration> jobConfiguration,
            ILogger<JobManager> logger)
        {
            EnsureArg.IsNotNull(jobStore, nameof(jobStore));
            EnsureArg.IsNotNull(taskExecutor, nameof(taskExecutor));
            EnsureArg.IsNotNull(fhirSpecificationProvider, nameof(fhirSpecificationProvider));
            EnsureArg.IsNotNull(schedulerConfiguration, nameof(schedulerConfiguration));
            EnsureArg.IsNotNull(jobConfiguration, nameof(jobConfiguration));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _jobStore = jobStore;
            _taskExecutor = taskExecutor;
            _fhirSpecificationProvider = fhirSpecificationProvider;
            _schedulerConfiguration = schedulerConfiguration.Value;
            _jobConfiguration = jobConfiguration.Value;
            _logger = logger;
        }

        /// <summary>
        /// Resume an active job or trigger new job from job store
        /// and execute the job.
        /// </summary>
        /// <param name="cancellationToken">cancellation token.</param>
        /// <returns>Completed task.</returns>
        public async Task TriggerJobAsync(CancellationToken cancellationToken = default)
        {
            // Acquire lock to ensure the job store is changed from only one client.
            var lockResult = await _jobStore.AcquireJobLock(cancellationToken);
            if (!lockResult)
            {
                // Acquire lock failed.
                _logger.LogWarning("Acquire job lock failed. Skipping this trigger.");
                return;
            }

            Job job;
            var activeJobs = await _jobStore.GetActiveJobsAsync();
            if (activeJobs.Any())
            {
                // Resume an active job.
                job = activeJobs.First();

                if (job.Status == JobStatus.Succeeded)
                {
                    _logger.LogWarning("Job '{id}' has already succeeded.", job.Id);
                    await _jobStore.CompleteJobAsync(job);
                    return;
                }

                // Resume an inactive/failed job.
                job.Status = JobStatus.Running;
                job.FailedReason = null;
            }
            else
            {
                // No active job available, start new trigger.
                job = await CreateNewJobTrigger(cancellationToken);
            }

            try
            {
                await ExecuteJob(job, cancellationToken);
            }
            catch (Exception exception)
            {
                job.Status = JobStatus.Failed;
                job.FailedReason = exception.ToString();
                await _jobStore.UpdateJobAsync(job, cancellationToken);
                _logger.LogError(exception, "Process job '{jobId}' failed.", job.Id);
                throw;
            }

            // release job lock.
            await _jobStore.ReleaseJobLock(cancellationToken);
        }

        private async Task<Job> CreateNewJobTrigger(CancellationToken cancellationToken = default)
        {
            var schedulerSetting = await _jobStore.GetSchedulerMetadata(cancellationToken);
            DateTimeOffset triggerStart = GetTriggerStartTime(schedulerSetting);
            if (triggerStart >= _jobConfiguration.EndTime)
            {
                _logger.LogInformation("Job has been scheduled to end.");
                throw new StartJobFailedException("Job has been scheduled to end.");
            }

            // End data period for this trigger
            DateTimeOffset triggerEnd = GetTriggerEndTime();

            if (triggerStart >= triggerEnd)
            {
                _logger.LogInformation("The start time '{triggerStart}' to trigger is in the future.", triggerStart);
                throw new StartJobFailedException($"The start time '{triggerStart}' to trigger is in the future.");
            }

            IEnumerable<string> resourceTypes = _jobConfiguration.ResourceTypeFilters;
            if (resourceTypes == null || !resourceTypes.Any())
            {
                resourceTypes = _fhirSpecificationProvider.GetAllResourceTypes();
            }

            var newJob = new Job(
                _jobConfiguration.ContainerName,
                JobStatus.New,
                resourceTypes,
                new DataPeriod(triggerStart, triggerEnd),
                DateTimeOffset.UtcNow);
            await _jobStore.UpdateJobAsync(newJob, cancellationToken);
            return newJob;
        }

        private DateTimeOffset GetTriggerStartTime(SchedulerMetadata schedulerSetting)
        {
            var lastScheduledTo = schedulerSetting?.LastScheduledTimestamp;
            return lastScheduledTo ?? _jobConfiguration.StartTime;
        }

        // Job end time could be null (which means runs forever) or a timestamp in the future like 2120/01/01.
        // In this case, we will create a job to run with end time earlier that current timestamp.
        // Also, FHIR data use processing time as lastUpdated timestamp, there might be some latency when saving to data store.
        // Here we add a JobEndTimeLatencyInMinutes latency to avoid data missing due to latency in creation.
        private DateTimeOffset GetTriggerEndTime()
        {
            // Add two minutes latency here to allow latency in saving resources to database.
            var nowEnd = DateTimeOffset.Now.AddMinutes(-1 * AzureBlobJobConstants.JobQueryLatencyInMinutes);
            if (_jobConfiguration.EndTime != null
                && nowEnd > _jobConfiguration.EndTime)
            {
                return _jobConfiguration.EndTime.Value;
            }
            else
            {
                return nowEnd;
            }
        }

        private async Task ExecuteJob(Job job, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Start executing job '{jobId}'.", job.Id);

            // Add cancellation for job execution.
            CancellationTokenSource executionTokenSource = new CancellationTokenSource();
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(executionTokenSource.Token, cancellationToken);

            var progress = new Progress<TaskContext>(async context =>
            {
                lock (_updateJobLock)
                {
                    // Should not update if the resource type has completed processing.
                    if (job.CompletedResources.Contains(context.ResourceType))
                    {
                        return;
                    }

                    job.ResourceProgresses[context.ResourceType] = context.ContinuationToken;
                    job.TotalResourceCounts[context.ResourceType] = context.SearchCount;
                    job.ProcessedResourceCounts[context.ResourceType] = context.ProcessedCount;
                    job.SkippedResourceCounts[context.ResourceType] = context.SkippedCount;
                    job.PartIds[context.ResourceType] = context.PartId;
                }

                // Update job store.
                await _jobStore.UpdateJobAsync(job, linkedTokenSource.Token);
            });

            var tasks = new List<Task<TaskResult>>();
            foreach (var resourceType in job.ResourceTypes)
            {
                if (tasks.Count >= _schedulerConfiguration.MaxConcurrencyCount)
                {
                    var finishedTask = await Task.WhenAny(tasks);
                    if (finishedTask.IsFaulted)
                    {
                        _logger.LogError("Process task failed.");
                        throw new ExecuteTaskFailedException("Task execution failed", finishedTask.Exception);
                    }

                    UpdateJobTaskResult(job, finishedTask.Result);
                    tasks.Remove(finishedTask);
                }

                var context = TaskContext.Create(resourceType, job);
                if (context.IsCompleted)
                {
                    _logger.LogInformation("Skipping completed resource '{resourceType}'.");
                    continue;
                }

                tasks.Add(Task.Run(async () => await _taskExecutor.ExecuteAsync(context, progress, linkedTokenSource.Token)));

                _logger.LogInformation("Start processing resource '{resourceType}'", resourceType);
            }

            try
            {
                var taskResults = await Task.WhenAll(tasks);
                foreach (var taskResult in taskResults)
                {
                    UpdateJobTaskResult(job, taskResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Process task failed.");
                throw new ExecuteTaskFailedException("Task execution failed", ex);
            }

            // Cancel on-going requests during execution (i.e. blob updates in async progress report).
            executionTokenSource.Cancel();

            // Update job status before committing data.
            await _jobStore.UpdateJobAsync(job, cancellationToken);
            await _jobStore.CommitJobDataAsync(job, cancellationToken);

            // Update job status.
            job.Status = JobStatus.Succeeded;
            await _jobStore.CompleteJobAsync(job, cancellationToken);
            _logger.LogInformation("Finish scheduling job '{jobId}'", job.Id);
        }

        private void UpdateJobTaskResult(Job job, TaskResult taskResult)
        {
            lock (_updateJobLock)
            {
                if (taskResult.IsCompleted)
                {
                    job.CompletedResources.Add(taskResult.ResourceType);
                }

                job.ResourceProgresses[taskResult.ResourceType] = taskResult.ContinuationToken;
                job.TotalResourceCounts[taskResult.ResourceType] = taskResult.SearchCount;
                job.ProcessedResourceCounts[taskResult.ResourceType] = taskResult.ProcessedCount;
                job.SkippedResourceCounts[taskResult.ResourceType] = taskResult.SkippedCount;
                job.PartIds[taskResult.ResourceType] = taskResult.PartId;
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();

            Dispose();
            #pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
            GC.SuppressFinalize(this);
            #pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            await _jobStore.ReleaseJobLock();
        }
    }
}