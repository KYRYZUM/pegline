namespace Pegline.Core.Models;

/// <summary>A virtual Library folder. Only top-level folders may contain subfolders.</summary>
public sealed class Folder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }

    public Folder Clone() => (Folder)MemberwiseClone();
}
