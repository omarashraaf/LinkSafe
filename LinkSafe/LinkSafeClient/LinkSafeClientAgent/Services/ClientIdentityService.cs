using System.Security.Principal;
using Shared.Models;

namespace LinkSafeClientAgent.Services;

public sealed class ClientIdentityService
{
    public ClientIdentityInfo GetCurrent()
    {
        var domainUser = WindowsIdentity.GetCurrent().Name;
        var username = Environment.UserName;
        var domain = Environment.UserDomainName;

        return new ClientIdentityInfo
        {
            DomainUser = domainUser,
            Username = username,
            Domain = domain,
            ComputerName = Environment.MachineName
        };
    }
}
