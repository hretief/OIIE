namespace Oiie.Sandbox.Api.Providers;

/// <summary>
/// The ENG panels reading the deployed ENG provider app.
///
/// The interesting work is what this cannot do. ENG holds no instrument
/// attributes and, by design, no maturity, so those fields come back null and
/// the panel shows them empty. Publication is answered instead by asking which
/// named version's changeset range covers the element — which is how ENG models
/// release, and produces an answer that cannot drift from the markers the way a
/// stored status would.
/// </summary>
public sealed class ProviderEngSource(EngProviderClient client) : IEngSource
{
    public async Task<IReadOnlyList<TwinView>> ListTwinsAsync(CancellationToken ct)
    {
        var twins = await client.GetITwinsAsync(ct);

        return twins.Select(t => new TwinView(
            t.ITwinId,
            t.Handle,
            // ENG has no Name. DisplayName is the platform's equivalent, and the
            // handle is a better fallback than an empty heading.
            t.DisplayName ?? t.Handle,
            t.Description,
            new DateTimeOffset(t.CreatedUtc, TimeSpan.Zero))).ToList();
    }

    public async Task<IReadOnlyList<SegmentView>> ListSegmentsAsync(Guid twinId, CancellationToken ct)
    {
        // Elements are scoped by iModel, not by twin, so the twin's models are
        // resolved first. A twin with no models is not an error: it is a project
        // nobody has drawn in yet.
        var models = await client.GetIModelsAsync(twinId, ct);

        var segments = new List<SegmentView>();

        foreach (var model in models)
        {
            var elements = await client.GetElementsAsync(model.IModelId, ct);

            if (elements.Count == 0)
            {
                continue;
            }

            var markers = await client.GetNamedVersionsAsync(model.IModelId, ct);

            segments.AddRange(elements.Select(e => new SegmentView(
                e.ECInstanceId,
                e.CodeValue,
                e.FederationGuid,
                e.IModelId,

                // ENG has no service-description field, so the write path
                // carries the panel's text as UserLabel. Reading it back from
                // the same place is what makes an edit survive a refresh --
                // reporting null here would blank a value ENG is holding.
                ServiceDescription: e.UserLabel,

                // Everything ENG genuinely does not model. Left null rather
                // than filled with a placeholder: an empty cell is a visible
                // gap, whereas an invented value is a lie the demo would have
                // to keep telling.
                UnitNumber: null,

                // The one attribute ENG does hold, under its own name.
                ClassKey: e.FullyQualifiedECClassName,

                RangeMinimum: null,
                RangeMaximum: null,
                ControlAction: null,
                PidReference: null,

                // No maturity: ENG stores none, and deriving a three-state value
                // from a two-state fact would invent the Shared tier.
                Maturity: null,

                PublishedInVersion: FindCoveringVersion(markers, e.ChangesetIndex),

                new DateTimeOffset(e.ModifiedUtc, TimeSpan.Zero))));
        }

        return segments
            .OrderBy(s => s.TagNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The earliest marker that covers an element, or null when it sits ahead of
    /// every one.
    ///
    /// A marker is pinned at a changeset, and everything at or below that
    /// position is inside it. The earliest such marker is reported rather than
    /// the latest, because the question a reviewer asks is "when was this first
    /// released", not "what is the newest release that still contains it".
    /// </summary>
    private static string? FindCoveringVersion(
        IReadOnlyList<EngNamedVersionDto> markers, int changesetIndex) =>
        markers
            .Where(m => m.ChangesetIndex >= changesetIndex)
            .OrderBy(m => m.ChangesetIndex)
            .FirstOrDefault()?.Name;

    /// <summary>
    /// Authors an element in ENG itself.
    ///
    /// Three things this has to reconcile, because ENG's write contract and the
    /// panel's form do not have the same shape.
    ///
    /// The iModel is required. ENG scopes elements by model and allocates a
    /// changeset position within one, so there is no model-less place to put an
    /// element. It is refused rather than guessed: picking a model on the
    /// caller's behalf would attribute the element to a source nobody chose.
    ///
    /// The class arrives as a name and ENG wants an ECClassId, so the name is
    /// resolved against ENG's own class list. An unresolvable name is refused
    /// rather than dropped, because an element created on the wrong class is
    /// harder to notice than one that was never created.
    ///
    /// The federation id is required on create and never minted here. ENG
    /// accepts the field but never invents it -- see IEngDesignStore -- and a
    /// server-minted identity would look exactly like one adopted from a tag
    /// register while binding to nothing. A create without one is refused, so
    /// the choice stays with the caller who knows whether the entity is
    /// already identified elsewhere.
    /// </summary>
    public async Task<AuthoredSegment> AddSegmentAsync(
        Guid twinId, NewSegment segment, CancellationToken ct)
    {
        if (segment.IModelId is not { } modelId || modelId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "An iModel is required: ENG stores elements within a model and cannot " +
                "place one without knowing which.");
        }

        if (string.IsNullOrWhiteSpace(segment.TagNumber))
        {
            // ENG allocates no codes. The sandbox's own series allocator is a
            // participant-database facility, and reaching into it here would
            // mint a code in one store for an element authored in another.
            throw new InvalidOperationException(
                "A segment number is required: ENG allocates no codes, so there is " +
                "nothing for it to name this element.");
        }

        var ecClassId = await ResolveClassAsync(segment.ClassKey, ct);

        var given = segment.FederationId is { } supplied && supplied != Guid.Empty
            ? supplied
            : (Guid?)null;

        // Required on create and on edit alike, and never minted here. The
        // column is nullable and ENG invents nothing, but an element with no
        // federated identity is one nothing downstream can bind -- so it is
        // refused at authoring time rather than allowed in and found
        // unbindable after publication.
        //
        // Enforced on edit too, because the create-time check alone would let
        // an element authored before this rule keep being saved without one:
        // every update would pass a null, ENG would read that as "not
        // mentioned", and the row would stay unidentified indefinitely. An
        // edit is the moment the gap can be closed, so it is where the rule
        // is applied.
        //
        // Minting silently would satisfy the rule and defeat its purpose: the
        // identity is meant to be the one the entity already has elsewhere, and
        // a server-invented guid is indistinguishable from that until someone
        // tries to reconcile the two systems. The panel offers a suggestion the
        // caller can adopt deliberately instead.
        if (given is null)
        {
            throw new InvalidOperationException(
                "A FederationGuid is required: an element with no federated " +
                "identity cannot be bound to the entity it represents.");
        }

        // Passed on every write, so a corrected value replaces the stored one
        // -- ENG's update coalesces, so a supplied guid overwrites and a null
        // would leave the old one standing. A first assignment can be wrong,
        // and this panel is the only place to fix it. The uniqueness index on
        // FederationGuid still refuses a rebinding onto an identity another
        // element already holds.
        var federationId = given;

        var result = await client.UpsertElementsAsync(
            modelId,
            [new EngElementUpsertDto(
                ECClassId: ecClassId,

                // The segment number is ENG's CodeValue: both are the
                // human-facing name, unique within their scope.
                CodeValue: segment.TagNumber,

                // ENG holds no service description field, so it is carried as
                // the label rather than discarded -- the one place the panel's
                // text survives a round trip.
                UserLabel: segment.ServiceDescription,
                DisplayName: segment.ServiceDescription,
                FederationGuid: federationId,

                // Present only on an edit. ENG matches an existing element by
                // this id and inserts when it is absent, so omitting it here
                // would author a second element under a code the first already
                // holds -- which ENG then refuses as a duplicate.
                ECInstanceId: segment.ElementId)],
            ct);

        // ENG answers 200 with per-item rejections, so success cannot be
        // inferred from the status. An unread rejection here is precisely the
        // "saved but never appeared" failure this path was written to end.
        if (result.Rejections.Count > 0)
        {
            throw new InvalidOperationException(
                $"ENG refused this element: {result.Rejections[0].Reason}");
        }

        if (result.Elements.Count == 0)
        {
            throw new InvalidOperationException(
                "ENG accepted the request but reported no element.");
        }

        return new AuthoredSegment(
            result.Elements[0].CodeValue ?? segment.TagNumber,
            federationId,
            twinId,
            modelId,

            // Null, not "WorkInProgress". ENG stores no maturity, and the read
            // path already reports none; inventing one on write would make the
            // panel disagree with itself the moment the list refreshed.
            Maturity: null);
    }

    /// <summary>
    /// Cuts a named version in ENG, which is how a release is expressed there.
    ///
    /// Three things follow from ENG's model rather than from the panel's.
    ///
    /// A marker is pinned within one iModel's history, so a twin-wide release is
    /// one marker per model. Membership is then derived from changeset position
    /// -- see dbo.vNamedVersionElement -- so nothing about which elements are
    /// included is sent, and nothing can be sent wrongly.
    ///
    /// Models with nothing new are skipped rather than marked. An empty marker
    /// is a real thing an engineer can cut, but cutting one automatically for
    /// every idle model would fill the history with markers nobody asked for and
    /// leave the engine drains examining them forever.
    ///
    /// Nothing is published here. Creating the marker is the entire act; the
    /// handover onto ISBM is EngEngine's, driven by its own poll. That is what
    /// makes the release survive the operator navigating away -- and it is why
    /// this path writes no outbox row where the sandbox path does.
    /// </summary>
    public async Task<PromotionOutcome> PromoteAsync(
        Guid twinId, string versionName, CancellationToken ct)
    {
        var models = await client.GetIModelsAsync(twinId, ct);

        var findings = new List<string>();
        var markers = new List<EngNamedVersionDto>();
        var released = 0;

        foreach (var model in models)
        {
            var elements = await client.GetElementsAsync(model.IModelId, ct);

            if (elements.Count == 0)
            {
                continue;
            }

            var existing = await client.GetNamedVersionsAsync(model.IModelId, ct);

            // Only what no marker already covers. Re-releasing an element that
            // has been handed over would republish it under a second version,
            // and receivers would have no way to tell that from a real change.
            var pending = elements
                .Where(e => FindCoveringVersion(existing, e.ChangesetIndex) is null)
                .ToList();

            if (pending.Count == 0)
            {
                continue;
            }

            // The gate, and it is ENG's rather than the sandbox's. ENG holds no
            // service description and no maturity, so those rules cannot be
            // checked here; what it does hold is federated identity, and an
            // element without one cannot be bound by any receiver -- see DR-017.
            var unfederated = pending.Where(e => e.FederationGuid is null).ToList();

            foreach (var element in unfederated)
            {
                findings.Add(
                    $"{element.CodeValue ?? element.ECInstanceId.ToString()}: " +
                    "NoFederationGuid — an element must carry a federated identity before release.");
            }

            if (unfederated.Count > 0)
            {
                continue;
            }

            markers.Add(await client.CreateNamedVersionAsync(
                new EngNamedVersionDraftDto(
                    model.IModelId,
                    versionName,
                    Description: null,

                    // ENG records who cut the marker. The sandbox has no signed-in
                    // principal to name, and inventing one would put a fabricated
                    // author on an immutable record.
                    CreatedBy: null),
                ct));

            released += pending.Count;
        }

        if (findings.Count > 0)
        {
            return new PromotionOutcome(
                false, 0, versionName, 0, 0, findings);
        }

        if (markers.Count == 0)
        {
            return new PromotionOutcome(
                false, 0, versionName, 0, 0, ["Nothing to publish."]);
        }

        return new PromotionOutcome(
            true,

            // The first marker's id. A twin-wide release can produce several and
            // the panel shows one, so MarkerCount is what says whether this is
            // the whole story.
            markers[0].NamedVersionId,
            versionName,
            released,
            markers.Count,
            []);
    }

    /// <summary>
    /// Turns a class name into the identifier ENG writes against.
    ///
    /// Matched on the fully qualified name, which is what the element picker
    /// offers and what an element reports when read back.
    /// </summary>
    private async Task<long> ResolveClassAsync(string? classKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(classKey))
        {
            throw new InvalidOperationException(
                "A class is required: ENG stores every element against one and has no " +
                "default to fall back on.");
        }

        var classes = await client.GetClassesAsync(ct);

        var match = classes.FirstOrDefault(c =>
            string.Equals(c.FullyQualifiedName, classKey, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            // Named in the message: the likely cause is a key from the
            // Sandbox's own reference data (rdl:*), which ENG has never heard
            // of, and saying which name failed makes that obvious.
            throw new InvalidOperationException(
                $"ENG holds no class '{classKey}'. Choose one of ENG's own classes.");
        }

        return match.ECClassId;
    }
}

/// <summary>
/// The stewardship panel reading the deployed REG-LOCATION provider app.
/// </summary>
public sealed class ProviderRegLocationSource(
    RegLocationProviderClient client,
    ILogger<ProviderRegLocationSource> logger) : IRegLocationSource
{
    public async Task<IReadOnlyList<StewardshipView>> GetQueueAsync(
        string? twin, bool includeDecided, CancellationToken ct)
    {
        // "All" has no single route: proposed rows come from the queue, decided
        // ones only from the full tag list.
        var rows = includeDecided
            ? (await client.GetTagsAsync(null, ct))
                .Select(t => new RegTagDetailDto(t, EmptyObject)).ToList()
            : (await client.GetProposedTagsAsync(ct)).ToList();

        if (!string.IsNullOrWhiteSpace(twin))
        {
            // The registry has no concept of an iTwin. The nearest equivalent is
            // the federation GUID on the paired registry row, so the filter is
            // applied against that when the caller named a GUID.
            if (Guid.TryParse(twin, out var twinGuid))
            {
                rows = rows.Where(r => r.Object.Guid == twinGuid).ToList();
            }
            else
            {
                // Anything else cannot be resolved against the registry. The
                // unfiltered queue is returned rather than an empty one, because
                // silently showing nothing would read as "no work outstanding".
                logger.LogWarning(
                    "Twin filter '{Twin}' is not a GUID; REG-LOCATION cannot scope by twin, "
                    + "so the unfiltered queue was returned.", twin);
            }
        }

        return rows.Select(r => new StewardshipView(
            r.Tag.TagId.ToString(),

            // The registry does not record who proposed a tag, only that it was
            // proposed. Naming the provider is accurate and more useful than an
            // empty column.
            SourceParticipant: "REG-LOCATION",
            SourceIdentifier: r.Tag.Code,
            ProposedName: r.Tag.Name,

            // Class degradation happens in the engine on the way in; the
            // registry records only the class it bound.
            RequestedClassKey: null,
            BoundClassKey: r.Tag.ClassId.ToString(),
            ClassDegraded: null,
            PropertiesMapped: null,
            PropertiesUnmapped: null,

            r.Tag.State,
            r.Object.DateAdded is { } added
                ? new DateTimeOffset(added, TimeSpan.Zero)
                : DateTimeOffset.MinValue,

            // The federation GUID is the only context the registry carries.
            r.Object.Guid?.ToString())).ToList();
    }

    public async Task<int> ApproveAsync(IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        // The registry approves one tag at a time and requires an accountable
        // decider, so a whole-queue approval is expanded here rather than sent
        // as a batch the provider does not accept.
        var targets = ids is { Count: > 0 }
            ? ids
            : (await client.GetProposedTagsAsync(ct)).Select(r => (long)r.Tag.TagId).ToList();

        var approved = 0;

        foreach (var id in targets)
        {
            // Registry tag ids are 32-bit. A sandbox id that will not narrow
            // belongs to a row the registry never saw, and approving some other
            // tag that happens to share the truncated value would be worse than
            // skipping it.
            if (id is < int.MinValue or > int.MaxValue)
            {
                logger.LogWarning("Skipping stewardship id '{Id}': not a registry tag id.", id);
                continue;
            }

            if (await client.ApproveTagAsync((int)id, "steward", ct) is not null)
            {
                approved++;
            }
        }

        return approved;
    }

    /// <summary>
    /// Stands in for the registry row on the "all" path, where the tag list
    /// route returns tags without their paired object.
    /// </summary>
    private static readonly RegObjectDto EmptyObject =
        new(0, 0, null, 0, null, null);
}
