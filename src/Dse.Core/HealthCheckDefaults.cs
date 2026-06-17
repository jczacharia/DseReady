// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse;

internal static class HealthCheckDefaults
{
    // Shared readiness-probe budget. Evaluated at health-check registration time (before options bind), so it is a
    // constant rather than a per-environment config knob — a probe timeout is not an operational dial.
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(8);
}
