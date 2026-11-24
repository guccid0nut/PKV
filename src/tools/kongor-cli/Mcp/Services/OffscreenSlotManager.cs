// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Allocated offscreen coordinate slot for an isolated headless game client.
/// </summary>
public sealed record OffscreenSlot
{
    public int SlotIndex { get; init; }
    public int Index => SlotIndex;
    public int X { get; init; }
    public int OffscreenX => X;
    public int Y { get; init; }
    public int OffscreenY => Y;
    public int ProcessId { get; init; }
    public string? AgentId { get; init; }
    public string DefaultNickname => SlotIndex == 0 ? ClientCredentialManager.PrimaryDefaultUsername : $"guest-{SlotIndex:D2}";
    public DateTimeOffset AllocatedAt { get; init; }
}

/// <summary>
/// Service interface for managing distinct offscreen coordinate positions for concurrent headless clients.
/// </summary>
public interface IOffscreenSlotManager
{
    OffscreenSlot AllocateSlot(int processId = 0, string? agentId = null, int? preferredSlot = null);
    OffscreenSlot AcquireSlot(int processId = 0, string? agentId = null, int? preferredSlot = null) => AllocateSlot(processId, agentId, preferredSlot);
    void AssignProcess(int slotIndex, int processId);
    void ReleaseSlot(int slotIndex);
    void ReleaseByProcessId(int processId);
    OffscreenSlot? GetSlotForProcess(int processId);
    IReadOnlyList<OffscreenSlot> GetActiveSlots();
    void PruneDeadProcesses();
}

/// <summary>
/// Dynamic Offscreen Coordinate Slot & Grid Allocator.
/// Assigns each headless client a unique, non-overlapping offscreen window coordinate
/// to prevent Win32 DWM occlusion, hit-test collisions, and visual bleed.
/// </summary>
public sealed class OffscreenSlotManager : IOffscreenSlotManager
{
    public static OffscreenSlotManager Default { get; } = new();

    public const int BaseX = -10000;
    public const int BaseY = -10000;
    public const int DeltaX = 2000;
    public const int DeltaY = 2000;
    public const int MaxColumns = 10;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, OffscreenSlot> _slotsByIndex = [];
    private readonly Dictionary<int, int> _processToSlotMap = [];
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public OffscreenSlotManager(TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [OffscreenSlotManager] {msg}"));
    }

    public OffscreenSlot AcquireSlot(int processId = 0, string? agentId = null, int? preferredSlot = null)
        => AllocateSlot(processId, agentId, preferredSlot);

    public OffscreenSlot AllocateSlot(int processId = 0, string? agentId = null, int? preferredSlot = null)
    {
        lock (_lock)
        {
            PruneDeadProcessesCore();

            // If this process already has a slot, reuse it
            if (processId > 0 && _processToSlotMap.TryGetValue(processId, out int existingIndex) && _slotsByIndex.TryGetValue(existingIndex, out var existingSlot))
            {
                return existingSlot;
            }

            // If preferredSlot is requested and available, use it
            int candidate;
            if (preferredSlot.HasValue && preferredSlot.Value >= 0 && !_slotsByIndex.ContainsKey(preferredSlot.Value))
            {
                candidate = preferredSlot.Value;
            }
            else
            {
                // Find lowest available slot index >= 0
                candidate = 0;
                while (_slotsByIndex.ContainsKey(candidate))
                {
                    candidate++;
                }
            }

            int col = candidate % MaxColumns;
            int row = candidate / MaxColumns;
            int x = BaseX + (col * DeltaX);
            int y = BaseY + (row * DeltaY);

            OffscreenSlot slot = new()
            {
                SlotIndex = candidate,
                X = x,
                Y = y,
                ProcessId = processId,
                AgentId = agentId,
                AllocatedAt = _timeProvider.GetUtcNow()
            };

            _slotsByIndex[candidate] = slot;
            if (processId > 0)
            {
                _processToSlotMap[processId] = candidate;
            }

            _logger($"Allocated Slot {candidate} at ({x}, {y}) for PID {processId} (Agent: '{agentId ?? "none"}'). Total active: {_slotsByIndex.Count}");
            return slot;
        }
    }

    public void AssignProcess(int slotIndex, int processId)
    {
        lock (_lock)
        {
            if (_slotsByIndex.TryGetValue(slotIndex, out var existing))
            {
                var updated = existing with { ProcessId = processId };
                _slotsByIndex[slotIndex] = updated;
                if (processId > 0)
                {
                    _processToSlotMap[processId] = slotIndex;
                }
                _logger($"Assigned PID {processId} to Slot {slotIndex}.");
            }
        }
    }

    public void ReleaseSlot(int slotIndex)
    {
        lock (_lock)
        {
            if (_slotsByIndex.Remove(slotIndex, out var slot))
            {
                if (slot.ProcessId > 0)
                {
                    _processToSlotMap.Remove(slot.ProcessId);
                }
                _logger($"Released Slot {slotIndex} at ({slot.X}, {slot.Y}) (PID {slot.ProcessId}). Remaining active: {_slotsByIndex.Count}");
            }
        }
    }

    public void ReleaseByProcessId(int processId)
    {
        if (processId <= 0) return;
        lock (_lock)
        {
            if (_processToSlotMap.Remove(processId, out int slotIndex))
            {
                _slotsByIndex.Remove(slotIndex, out _);
                _logger($"Released Slot {slotIndex} for PID {processId}. Remaining active: {_slotsByIndex.Count}");
            }
        }
    }

    public OffscreenSlot? GetSlotForProcess(int processId)
    {
        if (processId <= 0) return null;
        lock (_lock)
        {
            if (_processToSlotMap.TryGetValue(processId, out int slotIndex) && _slotsByIndex.TryGetValue(slotIndex, out var slot))
            {
                return slot;
            }
            return null;
        }
    }

    public IReadOnlyList<OffscreenSlot> GetActiveSlots()
    {
        lock (_lock)
        {
            return _slotsByIndex.Values.OrderBy(s => s.SlotIndex).ToList();
        }
    }

    public void PruneDeadProcesses()
    {
        lock (_lock)
        {
            PruneDeadProcessesCore();
        }
    }

    private void PruneDeadProcessesCore()
    {
        if (GameServerManageHandler.IsTestExecution) return;

        List<int> deadPids = [];
        foreach (var (pid, slotIndex) in _processToSlotMap)
        {
            if (pid <= 0) continue;
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    deadPids.Add(pid);
                }
            }
            catch (ArgumentException)
            {
                // Process is dead
                deadPids.Add(pid);
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Error probing PID {pid}: {ex.Message}");
            }
        }

        foreach (int pid in deadPids)
        {
            if (_processToSlotMap.Remove(pid, out int slotIdx))
            {
                _slotsByIndex.Remove(slotIdx);
                _logger($"Reclaimed abandoned Slot {slotIdx} from dead process PID {pid}.");
            }
        }
    }
}
