namespace ZyncMaster.Server;

public interface ISecretProvider
{
    string GetMicrosoftClientSecret();
}
