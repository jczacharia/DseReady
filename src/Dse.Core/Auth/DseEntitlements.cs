// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Auth;

public static class DseEntitlements
{
    public const string KibanaAdminOudDn = "cn=app-dse-kibana-admin,ou=Groups,o=pnc";
    public const string KibanaReadonlyOudDn = "cn=app-dse-kibana-user-readonly,ou=Groups,o=pnc";
}
