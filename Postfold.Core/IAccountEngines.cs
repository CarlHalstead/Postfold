namespace Postfold.Core;

public interface IAccountEngines
{
    IMailAccountEngine? Find(AccountId account);
}
