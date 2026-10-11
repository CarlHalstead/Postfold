using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IWorkPolicy
{
    bool AllowNetwork
    {
        get;
    }
    bool AllowOptionalWork
    {
        get;
    }
}
