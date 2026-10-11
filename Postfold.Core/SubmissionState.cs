using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public enum SubmissionState
{
    Queued, Submitting, Accepted, SentCopyPending, Completed, Rejected, PartiallyAccepted, Uncertain
}
