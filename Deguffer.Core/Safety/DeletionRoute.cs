namespace Deguffer.Core.Safety;

/// <summary>How <see cref="EntryDeletion"/> deletes one entry.</summary>
internal enum DeletionRoute
{
    /// <summary>Through the handle it opened, read-only or not.</summary>
    OneCall,

    /// <summary>Through <see cref="File.Delete"/> or <see cref="Directory.Delete(string)"/>, as before.</summary>
    Runtime,

    /// <summary>A folder asked for as a file, or a file as a folder, refused as the runtime refuses it.</summary>
    WrongKind,
}
