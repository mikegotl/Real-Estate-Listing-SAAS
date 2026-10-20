# Lead generation

Listing Studio uses first-party inbound lead capture. It does not scrape consumers, buy lead lists, automatically send
marketing messages, or disclose one organization's leads to another organization or to platform administrators.

## Agent workflow

1. Open a property and select the Campaign tab.
2. In Lead destination, add an optional headline and publish the public listing page.
3. Use the generated Facebook, Instagram, TikTok, YouTube, or QR link. Each link adds source attribution without
   changing the public listing.
4. New inquiries appear in the organization-scoped Lead Center and produce an in-app notification for organization
   members.
5. Move a lead through New, Contacted, Qualified, Showing scheduled, Nurture, Won, or Lost. Add private notes and a
   follow-up date, or permanently delete the lead and its activity history.
6. Export the current organization lead list as CSV when a portable copy is needed.

Unpublishing the lead destination immediately removes anonymous access to its page, cover photo, campaign video, and
QR destination. Existing leads remain private in the Lead Center until an agent deletes them.

## Privacy and consent

- An inquiry can be submitted without consenting to ongoing marketing.
- Email-marketing and SMS consent are separate, optional, unticked choices. The recorded consent version is stored with
  the inquiry.
- SMS consent requires a phone number; choosing phone as the preferred contact method also requires one.
- Public forms use bounded inputs, a honeypot, and a database-backed duplicate-submission window. Public slugs contain
  128 bits of random data and cannot be enumerated through sequential property IDs.
- All lead-management reads and writes require organization membership. Cross-organization access returns no record.
- Platform administration reports must remain aggregate-only and must not include lead contact information or messages.
- CSV values that could be interpreted as spreadsheet formulas are neutralized during export.

Production marketing email and SMS remain disabled. Before adding either provider, implement suppression lists,
unsubscribe processing, sender identity and postal-address configuration, delivery audit records, provider webhooks,
and jurisdiction-specific legal review. AI may draft agent-reviewed replies later, but must not autonomously send,
infer protected characteristics, or rank prospects using protected or proxy demographic data.

## Architecture

- `LeadCapturePage` controls the deliberately public surface for one organization-owned property.
- `PropertyLead` stores the inquiry, attribution, explicit consent choices, workflow state, and follow-up date.
- `LeadActivity` stores the append-only workflow and private-note history until the lead is deleted.
- Public media endpoints first resolve a currently published capture page and then open only the selected cover and most
  recent rendered campaign video.
- QRCoder creates SVG codes locally. QR generation does not call a paid service or disclose the destination URL to a
  third party.

Future integrations should be separate reviewable changes: production email notifications, CRM/webhook export, then
agent-approved AI reply drafts. SMS and automated outreach require a dedicated compliance review before implementation.
