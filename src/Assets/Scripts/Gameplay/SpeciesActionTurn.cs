using System;
using System.Collections.Generic;
using UnityEngine;

public enum SpeciesActionTargetOverride
{
    Default,
    Self,
    Ally,
    Enemy,
}

/// <summary>Ordered enemy actions to perform during one turn in its action sequence.</summary>
[Serializable]
public class SpeciesActionTurn
{
    public List<SpeciesActionType> Actions = new();

    [Tooltip("Target override for each action at the matching index. Missing entries use Default.")]
    public List<SpeciesActionTargetOverride> TargetOverrides = new();

    public SpeciesActionTargetOverride GetTargetOverride(int actionIndex)
    {
        if (TargetOverrides == null || actionIndex < 0 || actionIndex >= TargetOverrides.Count)
        {
            return SpeciesActionTargetOverride.Default;
        }

        return TargetOverrides[actionIndex];
    }

    public SpeciesActionTurn Clone()
    {
        return new SpeciesActionTurn
        {
            Actions = new List<SpeciesActionType>(Actions ?? new List<SpeciesActionType>()),
            TargetOverrides = new List<SpeciesActionTargetOverride>(
                TargetOverrides ?? new List<SpeciesActionTargetOverride>()),
        };
    }
}
