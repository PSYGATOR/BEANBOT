using System.Numerics;

namespace BeanBot.Protocol.Maps;

/// <summary>
/// Static verified metadata for The Skeld tasks, consoles, vents, and doors (source: Impostor Innersloth Data/maps/Skeld).
/// Ground Rule G7: wire-independent catalog values live alongside protocol IDs.
/// </summary>
public static class SkeldCatalog
{
    public sealed record TaskStepLocation(string Room, Vector2 Position, float UsableDistance = 1.0f);

    public sealed record TaskMetadata(
        byte TaskType,
        string Name,
        int MaxSteps,
        IReadOnlyList<TaskStepLocation> DefaultSteps);

    /// <summary>
    /// Representative Skeld console coordinates per TaskType byte for single/multi-step task execution.
    /// Source: Impostor/src/Impostor.Api/Innersloth/Data/maps/Skeld/tasks.json + Among Us Skeld map layout.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, TaskMetadata> Tasks = new Dictionary<byte, TaskMetadata>
    {
        [(byte)TaskTypes.SubmitScan] = new((byte)TaskTypes.SubmitScan, "SubmitScan", 1, new[]
        {
            new TaskStepLocation("MedBay", new Vector2(-7.3284f, -5.2476006f), 1.0f),
        }),
        [(byte)TaskTypes.PrimeShields] = new((byte)TaskTypes.PrimeShields, "PrimeShields", 1, new[]
        {
            new TaskStepLocation("Shields", new Vector2(7.526399f, -13.981199f), 1.0f),
        }),
        [(byte)TaskTypes.FuelEngines] = new((byte)TaskTypes.FuelEngines, "FuelEngines", 2, new[]
        {
            new TaskStepLocation("Storage", new Vector2(-2.2f, -13.6f), 1.0f),
            new TaskStepLocation("UpperEngine", new Vector2(-18.2f, -1.0f), 1.0f),
        }),
        [(byte)TaskTypes.ChartCourse] = new((byte)TaskTypes.ChartCourse, "ChartCourse", 1, new[]
        {
            new TaskStepLocation("Nav", new Vector2(17.9f, -3.2f), 1.0f),
        }),
        [(byte)TaskTypes.StartReactor] = new((byte)TaskTypes.StartReactor, "StartReactor", 1, new[]
        {
            new TaskStepLocation("Reactor", new Vector2(-21.792f, -5.5728f), 1.0f),
        }),
        [(byte)TaskTypes.SwipeCard] = new((byte)TaskTypes.SwipeCard, "SwipeCard", 1, new[]
        {
            new TaskStepLocation("Admin", new Vector2(5.6352005f, -8.632801f), 1.0f),
        }),
        [(byte)TaskTypes.ClearAsteroids] = new((byte)TaskTypes.ClearAsteroids, "ClearAsteroids", 1, new[]
        {
            new TaskStepLocation("Weapons", new Vector2(9.087601f, 1.812f), 1.2f),
        }),
        [(byte)TaskTypes.UploadData] = new((byte)TaskTypes.UploadData, "UploadData", 2, new[]
        {
            new TaskStepLocation("Cafeteria", new Vector2(1.4f, 4.7f), 1.0f),
            new TaskStepLocation("Admin", new Vector2(2.5079997f, -6.2652006f), 1.0f),
        }),
        [(byte)TaskTypes.InspectSample] = new((byte)TaskTypes.InspectSample, "InspectSample", 1, new[]
        {
            new TaskStepLocation("MedBay", new Vector2(-6.144f, -4.2599998f), 1.0f),
        }),
        [(byte)TaskTypes.EmptyChute] = new((byte)TaskTypes.EmptyChute, "EmptyChute", 2, new[]
        {
            new TaskStepLocation("LifeSupp", new Vector2(6.5f, -2.5f), 1.0f),
            new TaskStepLocation("Storage", new Vector2(-1.6f, -14.4f), 1.0f),
        }),
        [(byte)TaskTypes.EmptyGarbage] = new((byte)TaskTypes.EmptyGarbage, "EmptyGarbage", 2, new[]
        {
            new TaskStepLocation("Cafeteria", new Vector2(3.5f, 4.9f), 1.0f),
            new TaskStepLocation("Storage", new Vector2(-1.6f, -14.4f), 1.0f),
        }),
        [(byte)TaskTypes.AlignEngineOutput] = new((byte)TaskTypes.AlignEngineOutput, "AlignEngineOutput", 2, new[]
        {
            new TaskStepLocation("UpperEngine", new Vector2(-19.176f, -0.41442212f), 1.0f),
            new TaskStepLocation("LowerEngine", new Vector2(-19.150799f, -12.618f), 1.0f),
        }),
        [(byte)TaskTypes.FixWiring] = new((byte)TaskTypes.FixWiring, "FixWiring", 3, new[]
        {
            new TaskStepLocation("Electrical", new Vector2(-7.7279997f, -7.6668f), 1.0f),
            new TaskStepLocation("Storage", new Vector2(-1.932f, -8.7204f), 1.0f),
            new TaskStepLocation("Admin", new Vector2(1.392f, -6.4092f), 1.0f),
        }),
        [(byte)TaskTypes.CalibrateDistributor] = new((byte)TaskTypes.CalibrateDistributor, "CalibrateDistributor", 1, new[]
        {
            new TaskStepLocation("Electrical", new Vector2(-5.8644004f, -7.4772f), 1.0f),
        }),
        [(byte)TaskTypes.DivertPower] = new((byte)TaskTypes.DivertPower, "DivertPower", 2, new[]
        {
            new TaskStepLocation("Electrical", new Vector2(-9.0f, -7.2936006f), 1.0f),
            new TaskStepLocation("Shields", new Vector2(8.8f, -13.1f), 1.0f),
        }),
        [(byte)TaskTypes.UnlockManifolds] = new((byte)TaskTypes.UnlockManifolds, "UnlockManifolds", 1, new[]
        {
            new TaskStepLocation("Reactor", new Vector2(-22.536001f, -2.4996f), 1.0f),
        }),
        [(byte)TaskTypes.CleanO2Filter] = new((byte)TaskTypes.CleanO2Filter, "CleanO2Filter", 1, new[]
        {
            new TaskStepLocation("LifeSupp", new Vector2(5.7432f, -2.9772f), 0.8f),
        }),
        [(byte)TaskTypes.StabilizeSteering] = new((byte)TaskTypes.StabilizeSteering, "StabilizeSteering", 1, new[]
        {
            new TaskStepLocation("Nav", new Vector2(18.732f, -4.728f), 1.25f),
        }),
        [(byte)TaskTypes.VentCleaning] = new((byte)TaskTypes.VentCleaning, "VentCleaning", 1, new[]
        {
            new TaskStepLocation("Admin", new Vector2(2.544f, -9.955201f), 1.0f),
        }),
    };

    /// <summary>Maps Impostor SetTasks raw taskId bytes (0..29 from Skeld tasks.json) to TaskTypes bytes.</summary>
    public static byte MapSkeldTaskIndexToTaskType(byte skeldTaskIndex) => skeldTaskIndex switch
    {
        0 => (byte)TaskTypes.SwipeCard,
        1 => (byte)TaskTypes.FixWiring,
        2 => (byte)TaskTypes.ClearAsteroids,
        3 => (byte)TaskTypes.AlignEngineOutput,
        4 => (byte)TaskTypes.SubmitScan,
        5 => (byte)TaskTypes.InspectSample,
        6 => (byte)TaskTypes.FuelEngines,
        7 => (byte)TaskTypes.StartReactor,
        8 => (byte)TaskTypes.EmptyChute,
        9 => (byte)TaskTypes.EmptyGarbage,
        10 or 15 or 17 or 19 or 20 => (byte)TaskTypes.UploadData,
        11 => (byte)TaskTypes.CalibrateDistributor,
        12 => (byte)TaskTypes.ChartCourse,
        13 => (byte)TaskTypes.CleanO2Filter,
        14 => (byte)TaskTypes.UnlockManifolds,
        16 => (byte)TaskTypes.StabilizeSteering,
        18 => (byte)TaskTypes.PrimeShields,
        >= 21 and <= 28 => (byte)TaskTypes.DivertPower,
        29 => (byte)TaskTypes.VentCleaning,
        _ => skeldTaskIndex,
    };
}
