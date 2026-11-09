/**
 * Configurable permit-to-work checklists. Edit this file (or move it to a
 * database table) to match your organisation's procedures. Checklists record
 * what was verified; they do not certify regulatory compliance.
 */
export const PERMIT_CHECKLISTS: Record<string, { title: string; items: string[] }> = {
  hot_work: { title: 'Hot work', items: ['Area cleared of combustibles (11 m)', 'Fire extinguisher at location', 'Fire watch assigned (incl. 30 min after)', 'Gas test done where required', 'Welding screens in place', 'Cylinders upright, flashback arrestors fitted'] },
  work_at_height: { title: 'Work at height', items: ['Scaffold tagged / inspected', 'Full-body harness, double lanyard', 'Anchor points identified', 'Edge protection / guard rails', 'Area below barricaded', 'Rescue plan briefed'] },
  lifting: { title: 'Lifting operation', items: ['Lifting plan approved', 'Crane & tackle certificates valid', 'Load weight & radius checked against chart', 'Ground bearing / outriggers checked', 'Signaller and tag lines assigned', 'Exclusion zone barricaded'] },
  confined_space: { title: 'Confined space entry', items: ['Isolations in place', 'Gas test (O₂, LEL, toxic) recorded', 'Continuous ventilation', 'Attendant posted outside', 'Rescue equipment ready', 'Entry log maintained'] },
  excavation: { title: 'Excavation', items: ['Underground services cleared', 'Shoring / benching / sloping adequate', 'Spoil kept ≥1 m from edge', 'Safe access / egress', 'Barricading and lighting', 'Dewatering arrangement'] },
  electrical_isolation: { title: 'Electrical isolation (LOTO)', items: ['Isolation point identified', 'Lock and tag applied', 'Absence of voltage tested', 'Stored energy discharged', 'Earthing applied where required', 'Permit holder briefed'] },
  general: { title: 'General work permit', items: ['Job safety analysis reviewed', 'PPE verified', 'Toolbox talk held', 'Housekeeping arranged'] },
}
