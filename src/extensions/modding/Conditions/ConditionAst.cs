// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace Kongor.Modding.Conditions;

/// <summary>
///     Base class for AST condition nodes.
/// </summary>
public abstract record ConditionNode
{
    public abstract bool Evaluate(Func<string, string?> installedModVersionResolver);
}

/// <summary>
///     Evaluates presence and version range of a specific mod name.
/// </summary>
public sealed record ModPredicateNode(string ModName, VersionRangeExpression? VersionConstraint) : ConditionNode
{
    public override bool Evaluate(Func<string, string?> installedModVersionResolver)
    {
        string? installedVer = installedModVersionResolver(ModName);
        if (installedVer == null) return false;

        if (VersionConstraint == null) return true;

        return VersionConstraint.IsSatisfiedBy(installedVer);
    }
}

/// <summary>
///     Logical NOT operator.
/// </summary>
public sealed record NotNode(ConditionNode Child) : ConditionNode
{
    public override bool Evaluate(Func<string, string?> installedModVersionResolver)
    {
        return !Child.Evaluate(installedModVersionResolver);
    }
}

/// <summary>
///     Logical AND operator.
/// </summary>
public sealed record AndNode(ConditionNode Left, ConditionNode Right) : ConditionNode
{
    public override bool Evaluate(Func<string, string?> installedModVersionResolver)
    {
        return Left.Evaluate(installedModVersionResolver) && Right.Evaluate(installedModVersionResolver);
    }
}

/// <summary>
///     Logical OR operator.
/// </summary>
public sealed record OrNode(ConditionNode Left, ConditionNode Right) : ConditionNode
{
    public override bool Evaluate(Func<string, string?> installedModVersionResolver)
    {
        return Left.Evaluate(installedModVersionResolver) || Right.Evaluate(installedModVersionResolver);
    }
}

/// <summary>
///     Boolean constant (e.g. true / false).
/// </summary>
public sealed record ConstantNode(bool Value) : ConditionNode
{
    public override bool Evaluate(Func<string, string?> installedModVersionResolver)
    {
        return Value;
    }
}
