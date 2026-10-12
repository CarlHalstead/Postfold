using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Interfaces;

/// <summary>Looks up an existing account engine without opening a connection or provisioning an account.</summary>
public interface IAccountEngines
{
    IMailAccountEngine? Find(AccountId account);
}
