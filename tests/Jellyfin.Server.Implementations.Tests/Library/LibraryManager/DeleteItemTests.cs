using System;
using System.IO;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Tests for LibraryManager.DeleteItem() verifying file deletion behavior.
/// Tests use real filesystem operations to verify actual file/folder deletion.
/// </summary>
public sealed class DeleteItemTests
{
    private readonly Emby.Server.Implementations.Library.LibraryManager _libraryManager;
    private readonly string _testRoot;

    public DeleteItemTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());

        // Paths setup:
        // PathManager reads data/trickplay/metadata/temp from IServerApplicationPaths.
        // We define these paths so the mock can return them. Otherwise PathManager gets null and crashes.
        // The actual directories don't need to exist (code checks Directory.Exists first).
        _testRoot = Path.Combine(Path.GetTempPath(), $"jellyfin-test-{Guid.NewGuid():N}"[..25]);
        var dataPath = Path.Combine(_testRoot, "data");
        var trickplayPath = Path.Combine(dataPath, "trickplay");
        var metadataPath = Path.Combine(dataPath, "metadata");
        var tempPath = Path.Combine(dataPath, "temp");

        // Create a temp directory
        Directory.CreateDirectory(_testRoot);

        // Mock must return valid paths so PathManager doesn't crash on null
        var appPathsMock = fixture.Freeze<Mock<IServerApplicationPaths>>();
        appPathsMock.Setup(p => p.DataPath).Returns(dataPath);
        appPathsMock.Setup(p => p.TrickplayPath).Returns(trickplayPath);
        appPathsMock.Setup(p => p.InternalMetadataPath).Returns(metadataPath);
        appPathsMock.Setup(p => p.TempDirectory).Returns(tempPath);

        // Configuration mock
        var configMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configMock.Setup(c => c.ApplicationPaths).Returns(appPathsMock.Object);
        configMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        // Real PathManager and ManagedFileSystem
        fixture.Inject<IPathManager>(new PathManager(configMock.Object, appPathsMock.Object));
        fixture.Inject<IFileSystem>(new ManagedFileSystem(
            NullLogger<ManagedFileSystem>.Instance,
            appPathsMock.Object,
            Array.Empty<IShortcutHandler>()));

        // Mock repo
        fixture.Freeze<Mock<IItemRepository>>()
            .Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null!);

        // Mock MediaSourceManager
        fixture.Freeze<Mock<IMediaSourceManager>>()
            .Setup(m => m.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);

        // Real LibraryManager with all dependencies injected.
        _libraryManager = fixture.Create<Emby.Server.Implementations.Library.LibraryManager>();

        // This is pretty terrible but unavoidable
        // Static properties on BaseItem for service location.
        BaseItem.ConfigurationManager ??= configMock.Object;
        BaseItem.LibraryManager ??= _libraryManager;
        BaseItem.FileSystem ??= fixture.Create<IFileSystem>();
        BaseItem.MediaSourceManager ??= fixture.Create<IMediaSourceManager>();
        BaseItem.MediaSegmentManager ??= fixture.Create<MediaBrowser.Controller.MediaSegments.IMediaSegmentManager>();
        Video.RecordingsManager ??= fixture.Create<MediaBrowser.Controller.LiveTv.IRecordingsManager>();
    }

    /// <summary>
    /// Verifies that DeleteItem deletes the video file, sidecar files (.nfo, -thumb.jpg, .srt),
    /// and the sidecar .trickplay folder when DeleteFileLocation is true.
    /// </summary>
    [Fact]
    public void DeleteItem_ShouldDeleteVideoFileAndSidecars()
    {
        // Arrange
        var seasonFolder = Path.Combine(_testRoot, "shows", "MyShow", "Season 1");
        var videoPath = Path.Combine(seasonFolder, "Show S01E01.mkv");
        var nfoPath = Path.Combine(seasonFolder, "Show S01E01.nfo");
        var thumbPath = Path.Combine(seasonFolder, "Show S01E01-thumb.jpg");
        var srtPath = Path.Combine(seasonFolder, "Show S01E01.srt");
        var trickplayFolder = Path.Combine(seasonFolder, "Show S01E01.trickplay");
        Directory.CreateDirectory(seasonFolder);
        Directory.CreateDirectory(trickplayFolder);
        File.WriteAllText(videoPath, "video content");
        File.WriteAllText(Path.Combine(seasonFolder, "Show S01E01.nfo"), "<episodedetails></episodedetails>");
        File.WriteAllText(thumbPath, "thumbnail");
        File.WriteAllText(srtPath, "1\n00:00:01,000 --> 00:00:02,000\nHello");
        File.WriteAllText(Path.Combine(trickplayFolder, "tile.jpg"), "trickplay image");

        // DeleteFileLocation: true to delete the file on disk, not just remove it from the database.
        var deleteOptions = new DeleteOptions { DeleteFileLocation = true };
        var episode = new Episode { Id = Guid.NewGuid(), Path = videoPath, IsInMixedFolder = true };

        // Act
        // parent: null! because the episode has no parent item in this test scenario.
        // notifyParentItem: false because we don't need to trigger parent refresh in tests.
        _libraryManager.DeleteItem(episode, deleteOptions, parent: null!, notifyParentItem: false);

        // Assert
        Assert.False(File.Exists(videoPath), $"Video file should be deleted: {videoPath}");
        Assert.False(Directory.Exists(trickplayFolder), $"Trickplay folder should be deleted: {trickplayFolder}");
        Assert.False(File.Exists(nfoPath), $"NFO file should be deleted: {nfoPath}");
        Assert.False(File.Exists(thumbPath), $"Thumbnail should be deleted: {thumbPath}");
        Assert.False(File.Exists(srtPath), $"Subtitle should be deleted: {srtPath}");

        Directory.Delete(_testRoot, true);
    }
}
