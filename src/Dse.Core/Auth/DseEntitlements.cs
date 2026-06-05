// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public static class DseEntitlements
{
    public const string KibanaAdminOudDn = "cn=app-dse-kibana-admin,ou=Groups,o=pnc";
    public const string KibanaReadonlyOudDn = "cn=app-dse-kibana-user-readonly,ou=Groups,o=pnc";
}
