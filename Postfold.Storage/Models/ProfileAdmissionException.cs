using Postfold.Core.Enums;
using System.IO;

namespace Postfold.Storage.Models;

internal sealed class ProfileAdmissionException(Failure failure) : IOException("Profile admission failed: " + failure)
{
    internal Failure Failure { get; } = failure;
}
