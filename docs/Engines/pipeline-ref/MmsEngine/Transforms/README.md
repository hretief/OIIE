# Editing the mappings

This folder holds the rules that decide what gets written into your system when
a message arrives from another participant. If you know your own asset data, you
can own these files. You do not need to be a developer and you will not be
editing any program code.

---

## What you own, and what you don't

**Yours.** Which of your tables or resources a message lands in, which of your
fields each value goes to, which field identifies a record as "the same one
again", and which fields must never be overwritten once set.

**Not yours.** Retries, message ordering, what happens when your system is down,
talking to the message bus. That is all handled for you and is the same for every
participant. If something in this folder seems to be about *when* or *whether* a
message is processed, it is in the wrong place — say so.

---

## One-time setup

You need three things installed. Ask IT for these once; after that you will not
need anything else.

1. **Git** — how you get the files and send changes back.
2. **The .NET SDK** — so you can run the checks. You will type one command; you
   do not need to know what it does.
3. **A text editor** — VS Code is free and will colour the files usefully.

Then, once:

```
git clone <repository>
cd MmsEngine
dotnet test --filter Golden
```

If that last command prints a row of passes, you are set up correctly. **If it
does not work, stop and get it fixed before doing anything else.** Being able to
check your own work locally is the whole point — without it you will end up
emailing changes to a developer, which is exactly what this is meant to avoid.

---

## The loop

Every change, however small, goes round the same five steps.

### 1. Edit the mapping

Open the `transform.xslt` for the message you care about. `SyncSites` is the
simple one; start there even if your change is elsewhere, just to see the shape.

### 2. Update or add a test case

In the `fixtures/` folder next to the mapping, each test case is a pair:

```
fixtures/
  new-site.input.xml      a message as it arrives
  new-site.expected.xml   what should be written, as a result
```

If you changed what a field maps to, update the `expected` file to match. If you
are handling a situation nobody handled before — a site with no short name, a
segment of an unfamiliar class — add a new pair for it.

This is not bureaucracy. These files are what stop someone else's later change
from quietly undoing yours.

### 3. Run the checks

```
dotnet test --filter Golden
```

This runs every mapping against every test case. It does not touch your live
system, does not need a connection to anything, and takes a few seconds. A
failure prints the difference between what you expected and what came out.

Keep editing and re-running until it passes. Nobody sees the intermediate steps.

### 4. Send the change for review

```
git add .
git commit -m "MMS: map segment description to LIGHT_UNIT_DESC"
git push
```

Then open a pull request. A reviewer sees the mapping change and the test change
side by side, which is enough to judge it. They are checking that the mapping
says what you meant, not marking your code.

### 5. Deploy

Once approved, the change deploys automatically. On startup the engine re-runs
the same test cases against the real deployment and **refuses to start** if any
fail — so a broken mapping never reaches live data. If a deployment fails this
way, nothing was written and nothing is at risk.

---

## Anatomy of a mapping

Here is the whole `SyncSites` mapping, annotated. Everything else is a variation
on this.

```xml
<xsl:template match="ccom:Site">
  <Item sourceRef="{ccom:UUID}">

    <Op id="system"
        resource="LIGHT_SYSTEM_INVENTORY"   <!-- which of your tables -->
        mode="upsert"                        <!-- create, or update if present -->
        match="EXT_ASSET_ID">                <!-- how you recognise it again -->

      <Field name="EXT_ASSET_ID" onUpdate="never">
        <xsl:value-of select="ccom:UUID"/>
      </Field>

      <Field name="LIGHT_SYSTEM_NAME" onUpdate="set">
        <xsl:value-of select="ccom:ShortName"/>
      </Field>

      <Field name="CLASSIFICATION" onUpdate="setIfAbsent">Undetermined</Field>

    </Op>
  </Item>
</xsl:template>
```

Read it as: *for each site in the message, write one row into
`LIGHT_SYSTEM_INVENTORY`, recognised by `EXT_ASSET_ID`, with these three fields.*

`<xsl:value-of select="..."/>` means "take the value from this part of the
incoming message". A plain value like `Undetermined` means "always this".

---

## The three field rules

Every field needs an `onUpdate`. This is the setting you are most likely to get
wrong and the one that matters most, because it governs what happens the *second*
time a message about the same thing arrives — which will happen, routinely.

| Setting | Meaning | Use it for |
|---|---|---|
| `set` | Write it every time | Values the sender is authoritative about — names, descriptions, dimensions |
| `setIfAbsent` | Write it when creating the record; leave alone afterwards | Values your team may correct after the fact — classifications, categories |
| `never` | Only the identifier | The `match` field |

**Ask yourself: if someone in my team edits this field by hand, should the next
message from engineering wipe out their edit?**

If no, use `setIfAbsent`. That is why `CLASSIFICATION` is `setIfAbsent` above —
sites arrive as `Undetermined`, someone classifies them properly, and a
republished site must not undo that.

---

## Leaving a field out is not the same as blanking it

This trips people up, so it is worth stating plainly.

- **Field not in the mapping** → your system is never told anything about it.
  Whatever is there stays there.
- **Field in the mapping with an empty value** → your system is told to set it to
  blank. Whatever was there is erased.

`LIGHT_SYSTEM_ID` is deliberately absent from the segment mapping for this reason.
It records how your team groups light units — engineering has no opinion about it
and should not be able to clear it. Adding it to the mapping "for completeness"
would erase your team's grouping on every update.

**The rule: if the field describes how your organisation arranges its own work,
leave it out of the mapping entirely.**

---

## When a value has to be looked up

Some values are not in the message. The segment message carries a site reference,
but your system needs its own owner id for that site. That lookup is declared in
`resolutions.yaml`, next to the mapping:

```yaml
resolutions:
  - key: ownerId
    source: cir
    category: ITWIN-SITE
    cirIdFrom: //Segment/RegistrationSite/UUID
    onMiss: skip-item
```

Read as: *take the site's identifier from the message, look it up in the shared
registry, call the answer `ownerId`.* The mapping then uses it:

```xml
<Field name="OWNER_ID" onUpdate="set" ref="$ownerId"/>
```

`onMiss` says what to do when the registry has no answer:

| Value | Meaning |
|---|---|
| `skip-item` | Skip this one segment, process the rest, record why. **Use this unless you have a reason not to.** |
| `reject-message` | Discard the whole message. Only when the message is meaningless without the value. |
| `use-default` | Use a stated fallback. Only where a genuine default exists. |

There is no fallback for which table to write into, and there never will be.
Guessing a table is not a degraded result, it is wrong data in the wrong place —
and unlike a blank field, nobody will ever notice.

---

## Common errors

| Message | What it means |
|---|---|
| `field 'X' has no onUpdate` | Add one of the three settings above |
| `upsert without @match` | You have not said how to recognise an existing record. Without it, every redelivered message creates a duplicate |
| `no binding for resource 'X'` | The table name is misspelled, or it needs adding to `bindings.yaml` — ask a developer for that one |
| `match key must be onUpdate="never"` | The identifying field cannot also be something you update |
| `op 'unit' does not return 'nativeKey'` | The mapping registers an id that the write never produces |
| Test passes but nothing is written | Your `select` path probably matches nothing in the message. Check it against the `input.xml` fixture |

That last one is the dangerous one, because an empty result looks like success.
It is exactly what the test cases exist to catch — which is why step 2 is not
optional.

---

## When the mapping can't express what you need

Sometimes you will want a condition, a calculation, or a rule the mapping file
cannot state. That is expected and there is a proper route for it: a developer
writes that piece in code, behind the same interface, and your mapping stays
readable.

**Please raise it rather than working around it.** A mapping file bent into
knots to express a rule is harder to review, harder to trust, and much harder for
the next person. The line "this needs a coded rule" is a reasonable thing to say
and nobody will think less of the mapping for it.

---

## Who to ask

| Question | Who |
|---|---|
| Which of my fields should this value go to? | You. That is the judgement this whole arrangement exists to capture. |
| Adding a new table to `bindings.yaml` | A developer — it needs a new endpoint |
| Setup that won't work | IT, then a developer |
| Which shared-registry category to look in | Whoever governs the reference data |
| "Is this even the right structure?" | Raise it. A mapping that feels forced usually is. |
