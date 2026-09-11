using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// The items the user keeps, and the one place that writes them back.
///
/// <para>Apart from <see cref="SelectionService"/> for the reason <see cref="KeepStore"/> gives: a
/// protection and a selection fail in opposite directions, so they never share a writer.</para>
///
/// <para><b>The two changes are written in opposite orders, and each order is chosen by which way
/// its failure points.</b> Keeping narrows what Deguffer offers, so it takes effect first and is
/// saved second, as a tick does: the item is protected for the rest of this session whether or not
/// the file can be written. Releasing widens it, so it is saved first and takes effect only once it
/// is, as a source folder is: a release that cannot be saved must not offer an item this session
/// that the file still keeps.</para>
/// </summary>
public sealed class KeepService
{
    private readonly KeepStore _store;

    public KeepService(KeepStore store)
    {
        _store = store;
        Current = store.Load();
    }

    /// <summary>What is kept now, which is what every row is built against.</summary>
    public KeepList Current { get; private set; }

    /// <summary>
    /// Whether the saved keep list could not be read at startup, so this session starts keeping
    /// nothing and no change it makes is saved over that file. The pages say so in those words: the
    /// folder is writable, and the file is what needs attention. See <see cref="KeepStore"/>.
    /// </summary>
    public bool StoredListUnreadable => _store.RefusesToSave;

    /// <summary>
    /// Keep <paramref name="item"/>. Returns whether that reached disk, so a caller can say the item
    /// will be offered again after a restart rather than implying it will not.
    /// </summary>
    public bool Keep(KeptItem item)
    {
        Current = Current.With(item);

        return _store.Save(Current);
    }

    /// <summary>
    /// Stop keeping one provider's item. Returns whether that reached disk. Where a write failed, the
    /// item stays kept.
    ///
    /// <para>The one exception is <see cref="StoredListUnreadable"/>, where the release is applied
    /// anyway. Nothing this session keeps is in the file then, so the saved keep that refusing would
    /// protect does not exist, and refusing would only make a keep clicked by mistake impossible to
    /// undo until a restart.</para>
    /// </summary>
    public bool Release(string providerId, string key)
    {
        var released = Current.Without(providerId, key);

        if (!_store.Save(released) && !_store.RefusesToSave)
        {
            return false;
        }

        Current = released;

        return !_store.RefusesToSave;
    }
}
