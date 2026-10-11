using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record BlobReference(Guid Id, AccountId Account, long Length, string Sha256, bool Protected);
