using System;
using Hangfire;
using PMS.Application.Jobs;

namespace PMS.Application.Services.Impl;

/// <summary>Runs the OCR of a submitted check as a Hangfire background job, so the upload request returns at once.</summary>
public class HangfireVehicleVerificationQueue(IBackgroundJobClient jobs) : IVehicleVerificationQueue
{
    public void Enqueue(Guid verificationId) =>
        jobs.Enqueue<VehicleDocumentJobs>(j => j.ProcessAsync(verificationId));
}
