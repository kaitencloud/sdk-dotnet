// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
namespace KaitenCloud.Sdk.Management.Models;

// Null-shape validation of reused management DTOs, never rule/flag evaluation.
internal static class ManagementValueValidation
{
    internal static bool Invalid(global::KaitenCloud.Sdk.DefaultFlagVariant? value) => value switch
    {
        global::KaitenCloud.Sdk.BasicDefaultFlagVariant basic => basic.Value is null,
        global::KaitenCloud.Sdk.PercentageDefaultFlagVariant percentage => percentage.Distribution is null,
        global::KaitenCloud.Sdk.DateDefaultFlagVariant date => InvalidStep(date.Start) || InvalidStep(date.End),
        _ => true
    };
    internal static bool Invalid(global::KaitenCloud.Sdk.FlagTargeting? value)
    {
        if (value is null || value.Name is null || value.Rule is null) return true;
        return value switch
        {
            global::KaitenCloud.Sdk.BasicFlagTargeting basic => basic.Variant is null,
            global::KaitenCloud.Sdk.PercentageFlagTargeting percentage => percentage.Distribution is null,
            global::KaitenCloud.Sdk.DateFlagTargeting date => InvalidStep(date.Start) || InvalidStep(date.End),
            _ => true
        };
    }
    private static bool InvalidStep(global::KaitenCloud.Sdk.RolloutStep? step) => step is null || step.Variant is null;
}
