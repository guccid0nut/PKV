// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Linq;
using System.Threading.Tasks;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class DeveloperIssueEntityTests
{
    [Fact]
    public async Task DeveloperIssues_CanInsertQueryAndUpdate_WithAllFields()
    {
        // Arrange
        using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        var issueId = Guid.NewGuid();
        var fingerprint = "a1b2c3d4e5f678901234567890abcdef1234567890abcdef1234567890abcdef";

        var issue = new DeveloperIssue
        {
            Id = issueId,
            Fingerprint = fingerprint,
            Subsystem = IssueSubsystem.GameClient,
            Severity = IssueSeverity.Error,
            Status = IssueResolutionStatus.Open,
            Title = "Null pointer in CGX_UpdateFrame",
            RawMessage = "[ERROR] Access violation reading 0x0 at k2_x64.dll+0x3A20",
            NormalizedSignature = "[ERROR] Access violation reading <ADDR> at k2_x64.dll+0x3A20",
            StackTrace = "   at CGX_UpdateFrame() in k2_x64.dll:0x3A20\n   at Engine_Step()",
            FaultingModule = "k2_x64.dll",
            FaultingAddress = 0x140003A20,
            OccurrenceCount = 1,
            AssociatedIssueTrackerId = "ISSUE-163"
        };

        // Act
        context.DeveloperIssues.Add(issue);
        await context.SaveChangesAsync();

        // Assert - Query
        DeveloperIssue? retrieved = await context.DeveloperIssues
            .FirstOrDefaultAsync(x => x.Id == issueId);

        Assert.NotNull(retrieved);
        Assert.Equal(fingerprint, retrieved.Fingerprint);
        Assert.Equal(IssueSubsystem.GameClient, retrieved.Subsystem);
        Assert.Equal(IssueSeverity.Error, retrieved.Severity);
        Assert.Equal(IssueResolutionStatus.Open, retrieved.Status);
        Assert.Equal("k2_x64.dll", retrieved.FaultingModule);
        Assert.Equal(1, retrieved.OccurrenceCount);

        // Act - Update to AutoResolved with recipe receipt
        retrieved.Status = IssueResolutionStatus.AutoResolved;
        retrieved.ResolutionRecipeApplied = "CrashDetourRecipe";
        retrieved.ResolutionReceipt = "Detour at 0x3A20 applied (backup: k2_x64.dll.bak)";
        retrieved.OccurrenceCount = 2;
        retrieved.LastSeenUtc = TimeProvider.System.GetUtcNow();
        await context.SaveChangesAsync();

        DeveloperIssue? updated = await context.DeveloperIssues.FindAsync(issueId);
        Assert.NotNull(updated);
        Assert.Equal(IssueResolutionStatus.AutoResolved, updated.Status);
        Assert.Equal("CrashDetourRecipe", updated.ResolutionRecipeApplied);
        Assert.Equal(2, updated.OccurrenceCount);
    }

    [Fact]
    public async Task DeveloperIssues_FiltersByStatusAndSubsystem()
    {
        // Arrange
        using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();

        context.DeveloperIssues.AddRange(
            new DeveloperIssue
            {
                Id = Guid.NewGuid(),
                Fingerprint = "fp1",
                Subsystem = IssueSubsystem.GameClient,
                Status = IssueResolutionStatus.Open,
                Title = "Client error 1"
            },
            new DeveloperIssue
            {
                Id = Guid.NewGuid(),
                Fingerprint = "fp2",
                Subsystem = IssueSubsystem.DedicatedServer,
                Status = IssueResolutionStatus.ActionRequired,
                Title = "Server error 1"
            },
            new DeveloperIssue
            {
                Id = Guid.NewGuid(),
                Fingerprint = "fp3",
                Subsystem = IssueSubsystem.GameClient,
                Status = IssueResolutionStatus.AutoResolved,
                Title = "Client error 2 resolved"
            }
        );
        await context.SaveChangesAsync();

        // Act & Assert
        var clientIssues = await context.DeveloperIssues
            .Where(x => x.Subsystem == IssueSubsystem.GameClient)
            .ToListAsync();
        Assert.Equal(2, clientIssues.Count);

        var pendingIssues = await context.DeveloperIssues
            .Where(x => x.Status == IssueResolutionStatus.Open || x.Status == IssueResolutionStatus.ActionRequired)
            .ToListAsync();
        Assert.Equal(2, pendingIssues.Count);
    }
}
