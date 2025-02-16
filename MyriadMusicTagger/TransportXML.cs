/* 
 Licensed under the Apache License, Version 2.0

 http://www.apache.org/licenses/LICENSE-2.0
 */

using System.Xml.Serialization;

namespace MyriadMusicTagger;

[XmlRoot(ElementName = "title")]
public class Title
{
    [XmlElement(ElementName = "title")] public string trackTitle { get; set; }

    [XmlElement(ElementName = "id")] public int Id { get; set; }
}

[XmlRoot(ElementName = "artist")]
public class Artist
{
    [XmlElement(ElementName = "name")] public string Name { get; set; }

    [XmlElement(ElementName = "id")] public int Id { get; set; }

    [XmlAttribute(AttributeName = "index")]
    public int Index { get; set; }
}

[XmlRoot(ElementName = "artists")]
public class Artists
{
    [XmlElement(ElementName = "artist")] public List<Artist> Artist { get; set; }
}

[XmlRoot(ElementName = "album")]
public class Album
{
    [XmlElement(ElementName = "title")] public string Title { get; set; }

    [XmlElement(ElementName = "id")] public int Id { get; set; }
}

[XmlRoot(ElementName = "itemAttribute")]
public class ItemAttribute
{
    [XmlElement(ElementName = "number")] public string Number { get; set; }

    [XmlElement(ElementName = "level")] public string Level { get; set; }

    [XmlAttribute(AttributeName = "stationId")]
    public string StationId { get; set; }

    [XmlAttribute(AttributeName = "type")] public string Type { get; set; }

    [XmlAttribute(AttributeName = "index")]
    public string Index { get; set; }
}

[XmlRoot(ElementName = "itemAttributes")]
public class ItemAttributes
{
    [XmlElement(ElementName = "itemAttribute")]
    public List<ItemAttribute> ItemAttribute { get; set; }
}

[XmlRoot(ElementName = "mediaLength")]
public class MediaLength
{
    [XmlElement(ElementName = "end")] public string End { get; set; }
}

[XmlRoot(ElementName = "extro")]
public class Extro
{
    [XmlElement(ElementName = "start")] public string Start { get; set; }
}

[XmlRoot(ElementName = "timings")]
public class Timings
{
    [XmlElement(ElementName = "mediaLength")]
    public MediaLength MediaLength { get; set; }

    [XmlElement(ElementName = "extro")] public Extro Extro { get; set; }

    [XmlElement(ElementName = "referenceLength")]
    public string ReferenceLength { get; set; }

    [XmlElement(ElementName = "totalLength")]
    public string TotalLength { get; set; }
}

[XmlRoot(ElementName = "custom")]
public class Custom
{
    [XmlAttribute(AttributeName = "index")]
    public string Index { get; set; }

    [XmlText] public string Text { get; set; }
}

[XmlRoot(ElementName = "copyright")]
public class Copyright
{
    [XmlElement(ElementName = "startOffset")]
    public string StartOffset { get; set; }

    [XmlElement(ElementName = "copyrightTitle")]
    public string CopyrightTitle { get; set; }

    [XmlElement(ElementName = "performer")]
    public string Performer { get; set; }

    [XmlElement(ElementName = "composer")] public string Composer { get; set; }

    [XmlElement(ElementName = "lyricist")] public string Lyricist { get; set; }

    [XmlElement(ElementName = "promoter")] public string Promoter { get; set; }

    [XmlElement(ElementName = "publisher")]
    public string Publisher { get; set; }

    [XmlElement(ElementName = "isrc")] public string Isrc { get; set; }

    [XmlElement(ElementName = "recordingNumber")]
    public string RecordingNumber { get; set; }

    [XmlElement(ElementName = "recordLabel")]
    public string RecordLabel { get; set; }

    [XmlElement(ElementName = "prs")] public string Prs { get; set; }

    [XmlElement(ElementName = "license")] public string License { get; set; }

    [XmlElement(ElementName = "address")] public string Address { get; set; }

    [XmlElement(ElementName = "custom")] public List<Custom> Custom { get; set; }
}
    
[XmlRoot(ElementName = "images")]
public class Images
{
    [XmlElement(ElementName = "image")] public List<Image> Image { get; set; }
}

[XmlRoot(ElementName = "image")]
public class Image
{
    [XmlElement(ElementName = "location")] public string Location { get; set; }
    [XmlElement(ElementName = "smallThumbnailLocation")] public string smallThumbnailLocation { get; set; }
    [XmlElement(ElementName = "largeThumbnailLocation")] public string largeThumbnailLocation { get; set; }
}

[XmlRoot(ElementName = "item")]
public class Item
{
    [XmlElement(ElementName = "value")] public string Value { get; set; }

    [XmlAttribute(AttributeName = "index")]
    public string Index { get; set; }
}

[XmlRoot(ElementName = "customFields")]
public class CustomFields
{
    [XmlElement(ElementName = "item")] public List<Item> Item { get; set; }
}

[XmlRoot(ElementName = "mediaItem")]
public class MediaItem
{
    [XmlElement(ElementName = "mediaId")] public string MediaId { get; set; }

    [XmlElement(ElementName = "guid")] public string Guid { get; set; }

    [XmlElement(ElementName = "externalReference")]
    public string ExternalReference { get; set; }

    [XmlElement(ElementName = "mediaLocation")]
    public string MediaLocation { get; set; }

    [XmlElement(ElementName = "originalMediaLocation")]
    public string OriginalMediaLocation { get; set; }

    [XmlElement(ElementName = "contentExists")]
    public string ContentExists { get; set; }

    [XmlElement(ElementName = "isSweeper")]
    public string IsSweeper { get; set; }

    [XmlElement(ElementName = "revision")] public string Revision { get; set; }

    [XmlElement(ElementName = "title")] public Title Title { get; set; }

    [XmlElement(ElementName = "itemDescription")]
    public string ItemDescription { get; set; }

    [XmlElement(ElementName = "artists")] public Artists Artists { get; set; }

    [XmlElement(ElementName = "album")] public Album Album { get; set; }

    [XmlElement(ElementName = "audioFormat")]
    public string AudioFormat { get; set; }

    [XmlElement(ElementName = "categoryName")]
    public string CategoryName { get; set; }

    [XmlElement(ElementName = "itemAttributes")]
    public ItemAttributes ItemAttributes { get; set; }

    [XmlElement(ElementName = "firstReleaseYear")]
    public string? FirstReleaseYear { get; set; }

    [XmlElement(ElementName = "timings")] public Timings Timings { get; set; }

    [XmlElement(ElementName = "copyright")]
    public Copyright Copyright { get; set; }
    
    [XmlElement(ElementName = "Images")] public Images Images{ get; set; }

    [XmlElement(ElementName = "customFields")]
    public CustomFields CustomFields { get; set; }

    [XmlElement(ElementName = "outCue")] public string OutCue { get; set; }

    [XmlElement(ElementName = "ending")] public string Ending { get; set; }

    [XmlElement(ElementName = "created")] public string Created { get; set; }

    [XmlElement(ElementName = "lastModified")]
    public string LastModified { get; set; }

    [XmlAttribute(AttributeName = "contentType")]
    public string ContentType { get; set; }

    [XmlAttribute(AttributeName = "type")] public string Type { get; set; }
}