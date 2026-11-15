using System.Numerics;

namespace BeanBot.World;

public sealed class AssignedTask
{
    public required uint TaskId { get; init; }
    public required string TaskTypeName { get; init; }
    public required string Room { get; init; }
    public required IReadOnlyList<Vector2> StepPositions { get; init; }
    public int CurrentStep { get; set; }
    public bool IsCompleted { get; set; }

    public int TotalSteps => Math.Max(1, StepPositions.Count);

    public Vector2 CurrentTargetPosition =>
        StepPositions.Count == 0
            ? Vector2.Zero
            : StepPositions[Math.Clamp(CurrentStep, 0, StepPositions.Count - 1)];
}
