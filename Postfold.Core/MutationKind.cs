using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public enum MutationKind
{
    SetRead, SetFlagged, Move
}
