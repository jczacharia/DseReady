// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Wolverine.EntityFrameworkCore;

namespace Dse.Ingestion;

public sealed class IngestRunCreatedHandler
{
    public static async Task Handle(IDbContextOutbox<DataContext> outlet)
    {

    }
}
