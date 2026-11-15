using System.Runtime.CompilerServices;

// Only the sibling runtime coordinates direct native writes with a prepared ledger publication.
[assembly: InternalsVisibleTo("APIShared")]
[assembly: InternalsVisibleTo("APISharedTests")]
[assembly: InternalsVisibleTo("Content.Tests")]
[assembly: InternalsVisibleTo("Core.Tests")]
