using Postfold.Core.Enums;

namespace Postfold.Core.Interfaces;

/// <summary>The native notification port accepts no message content, account name or recipient information.</summary>
public interface INewMailNotification
{
    Failure ShowGeneric();
}
