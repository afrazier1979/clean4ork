# Fixture notes

Save raw HTML here from the browser (View Source → save, or curl) — the
parser must be developed against raw HTML, not rendered/extracted text.

## Live-verified URL structure (2026-07)

Full report (anonymous access, userID=0):
https://philadelphia-pa.healthinspections.us/_templates/551/RetailFood/_report_full.cfm?inspectionID=393CF171-BCB1-20AD-90E30FAA73A99AFE&domainID=551&userID=0

Example above: "A Love Supreme Vegan", 6700 N Germantown Ave 19119,
inspected 2025-03-28 — a fully clean report (all items IN). Good as the
"perfect score" fixture.

## Confirmed report structure

- Header: facility name, address+ZIP, phone, establishment type,
  district/sub, licensee, corporate officer, purpose, inspection type,
  date, time in / time out
- FIRF matrix: items 1–27, grouped under headings (Demonstration of
  Knowledge, Employee Health, ... Conformance with Approved Procedure)
- GRP matrix: items 28–54 (Safe Food and Water ... Physical Facilities)
- Philadelphia Ordinances: items 55/56+
- Each row: item number, IN/OUT (or N/A, N/O), COS column, R column
- Temperature observations: Item/Location + Temp triplets
- Food Disposal table
- "OBSERVATIONS AND CORRECTIVE ACTIONS": violation text keyed by item number
- Remarks, Summary Statements (license eligibility notes)
- Person in Charge + Inspector signature blocks (name, phone, dates)

## Wanted fixtures (save 4–5)

- [ ] Clean routine inspection (candidate above)
- [ ] Routine with FIRF violations incl. COS and R flags
- [ ] Reinspection
- [ ] Complaint
- [ ] search.cfm results page for one ZIP (note the URL from address bar!)
- [ ] estab.cfm page for one facility (note the URL from address bar!)
