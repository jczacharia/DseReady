// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public static class DseEntitlements
{
    public const string KibanaAdminOudDn = "app-dse-kibana-admin";
    public const string KibanaReadonlyOudDn = "app-dse-kibana-user-readonly";
}
