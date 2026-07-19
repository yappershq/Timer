using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Shared.Interfaces.Modules;

/// <summary>
/// Extended replay interface for the playback module.
/// Inherits <see cref="IReplayModule"/> to maintain external API compatibility,
/// and adds internal methods for the recorder module to notify about new replays.
/// </summary>
public interface IReplayPlaybackModule : IReplayModule
{
    /// <summary>
    /// Called by the recorder when a new main replay is saved.
    /// Compares the new replay against the cached one and only overwrites if the new one is faster.
    /// </summary>
    /// <returns>true if the cache was updated (new replay is better or cache was empty), false otherwise.</returns>
    bool OnNewMainReplaySaved(int style, int track, ReplayContent content, ReplaySaveContext context);

    /// <summary>
    /// Called by the recorder when a new stage replay is saved.
    /// Compares the new replay against the cached one and only overwrites if the new one is faster.
    /// </summary>
    /// <returns>true if the cache was updated (new replay is better or cache was empty), false otherwise.</returns>
    bool OnNewStageReplaySaved(int style, int track, int stage, ReplayContent content, ReplaySaveContext context);
}
