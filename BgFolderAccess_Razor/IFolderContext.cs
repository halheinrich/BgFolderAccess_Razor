namespace BgFolderAccess_Razor;

/// <summary>
/// One folder a pick landed, held by the browser for as long as this context lives. Every
/// operation on the folder goes through the context that landed it, so two picks are two
/// contexts, and a host keeps a folder for as long as it holds something built from it
/// — a run recording into it, say — across a later pick of another, then releases it by
/// disposing the context. The browser's directory handle never crosses interop: it lives
/// in the JS module's table, keyed by this context.
///
/// <para>
/// The named-file members need the File System Access handle; on a fallback pick
/// (<see cref="FolderWriteCapability.BrowserUnsupported"/>) they throw
/// <see cref="Microsoft.JSInterop.JSException"/>. After <see cref="IAsyncDisposable.DisposeAsync"/>
/// every member throws <see cref="ObjectDisposedException"/>; disposing twice is harmless.
/// Expected outcomes are values: a missing file is a <c>null</c> read, a file the browser
/// will not write is <see cref="FileWritability.NotWritable"/>; unexpected browser
/// failures surface as <see cref="Microsoft.JSInterop.JSException"/>.
/// </para>
/// </summary>
public interface IFolderContext : IAsyncDisposable
{
    /// <summary>The folder's leaf name, as the browser reports it.</summary>
    string Name { get; }

    /// <summary>Whether the host can write files into this folder, settled once by the pick that landed it.</summary>
    FolderWriteCapability Capability { get; }

    /// <summary>
    /// Draw the folder again at the configured <see cref="FolderPickLimits"/>, with no picker
    /// and no permission prompt: a File System Access folder is walked afresh, a fallback
    /// folder's retained candidates are redrawn. All or nothing: a failure throws and returns
    /// no part of a sample, and the folder stays held. The write capability is not asked again.
    /// </summary>
    /// <exception cref="PickedFileTooLargeException">A drawn file is over <see cref="FolderPickLimits.MaxFileBytes"/>.</exception>
    /// <exception cref="Microsoft.JSInterop.JSException">The walk was refused, a read failed, or the browser failed otherwise.</exception>
    /// <exception cref="ObjectDisposedException">The context has been released.</exception>
    Task<FolderSample> ResampleAsync();

    /// <summary>Read the named file's text; <c>null</c> when no such file exists, which is not an error.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is null.</exception>
    /// <exception cref="Microsoft.JSInterop.JSException">The folder has no File System Access handle, or the browser failed the read.</exception>
    /// <exception cref="ObjectDisposedException">The context has been released.</exception>
    Task<string?> ReadFileAsync(string fileName);

    /// <summary>Write <paramref name="text"/> as the named file, creating it or replacing its content.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="Microsoft.JSInterop.JSException">The folder has no File System Access handle, or the browser failed the write.</exception>
    /// <exception cref="ObjectDisposedException">The context has been released.</exception>
    Task WriteFileAsync(string fileName, string text);

    /// <summary>
    /// Ask whether the named file can be written, without writing, creating or prompting: the
    /// browser opens it for writing and the stream is aborted. A file another program holds
    /// open answers <see cref="FileWritability.Writable"/> and fails only at a real write.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is null.</exception>
    /// <exception cref="Microsoft.JSInterop.JSException">The folder has no File System Access handle, or the browser failed the probe in a way that is not one of its answers.</exception>
    /// <exception cref="ObjectDisposedException">The context has been released.</exception>
    Task<FileWritability> ProbeFileWritabilityAsync(string fileName);
}
