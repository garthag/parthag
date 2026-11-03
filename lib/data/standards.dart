// Reference catalogues for Indian construction sites.
//
// Units follow IS 1200 (Method of Measurement of Building & Civil
// Engineering Works), concrete grades follow IS 456:2000, materials list the
// governing BIS standard, and safety items follow IS 7969 / BOCW Act 1996.

class LabourCategory {
  const LabourCategory(this.name, this.hindi, this.skill);
  final String name;
  final String hindi;
  final String skill;

  String get display => '$name ($hindi)';
}

class MaterialSpec {
  const MaterialSpec(this.name, this.specification, this.unit);
  final String name;
  final String specification;
  final String unit;
}

class ActivitySpec {
  const ActivitySpec(this.description, this.unit, this.reference);
  final String description;
  final String unit;
  final String reference;
}

class TestSpec {
  const TestSpec(this.name, this.standard);
  final String name;
  final String standard;
}

abstract final class Standards {
  /// Units of measurement (IS 1200).
  static const units = [
    'cum', 'sqm', 'rmt', 'MT', 'kg', 'nos', 'bags', 'ltr', 'set', 'LS', 'pair', 'trip',
  ];

  static const skillLevels = ['Highly skilled', 'Skilled', 'Semi-skilled', 'Unskilled'];

  static const labour = [
    LabourCategory('Site Engineer', 'साइट इंजीनियर', 'Highly skilled'),
    LabourCategory('Supervisor', 'सुपरवाइज़र', 'Highly skilled'),
    LabourCategory('Foreman', 'फोरमैन', 'Skilled'),
    LabourCategory('Mason', 'मिस्त्री', 'Skilled'),
    LabourCategory('Carpenter (Shuttering)', 'बढ़ई', 'Skilled'),
    LabourCategory('Bar bender', 'सरिया बेंडर', 'Skilled'),
    LabourCategory('Fitter', 'फिटर', 'Skilled'),
    LabourCategory('Welder', 'वेल्डर', 'Skilled'),
    LabourCategory('Electrician', 'इलेक्ट्रीशियन', 'Skilled'),
    LabourCategory('Plumber', 'प्लंबर', 'Skilled'),
    LabourCategory('Painter', 'पेंटर', 'Skilled'),
    LabourCategory('Tile fixer', 'टाइल मिस्त्री', 'Skilled'),
    LabourCategory('Machine operator', 'ऑपरेटर', 'Skilled'),
    LabourCategory('Driver', 'ड्राइवर', 'Semi-skilled'),
    LabourCategory('Rigger', 'रिगर', 'Semi-skilled'),
    LabourCategory('Helper', 'हेल्पर', 'Semi-skilled'),
    LabourCategory('Beldar / Mazdoor', 'बेलदार / मज़दूर', 'Unskilled'),
    LabourCategory('Coolie (Female)', 'रेजा', 'Unskilled'),
    LabourCategory('Watchman', 'चौकीदार', 'Unskilled'),
    LabourCategory('Safety Steward', 'सेफ्टी स्टीवर्ड', 'Skilled'),
  ];

  static const machinery = [
    'JCB / Backhoe loader',
    'Excavator (Poclain)',
    'Tower crane',
    'Mobile crane / Hydra',
    'Transit mixer',
    'Concrete pump',
    'Boom placer',
    'Batching plant',
    'Concrete mixer (1 bag)',
    'Needle vibrator',
    'Plate compactor',
    'Vibratory roller',
    'Tipper / Dumper',
    'Tractor with trolley',
    'Bar bending machine',
    'Bar cutting machine',
    'Builder hoist / Passenger lift',
    'DG set',
    'Welding machine',
    'Water tanker',
    'Dewatering pump',
    'Total station',
    'Auto level',
  ];

  static const ownership = ['Owned', 'Hired', 'Client supplied', 'Sub-contractor'];

  static const materials = [
    MaterialSpec('Cement OPC 53 Grade', 'IS 269:2015', 'bags'),
    MaterialSpec('Cement OPC 43 Grade', 'IS 269:2015', 'bags'),
    MaterialSpec('Cement PPC', 'IS 1489 (Part 1)', 'bags'),
    MaterialSpec('Cement PSC', 'IS 455:2015', 'bags'),
    MaterialSpec('TMT Steel Fe500D', 'IS 1786:2008', 'MT'),
    MaterialSpec('TMT Steel Fe550D', 'IS 1786:2008', 'MT'),
    MaterialSpec('Binding wire', 'IS 280', 'kg'),
    MaterialSpec('Ready Mix Concrete', 'IS 4926:2003', 'cum'),
    MaterialSpec('Fine aggregate (River sand)', 'IS 383:2016 Zone II', 'cum'),
    MaterialSpec('Manufactured sand (M-Sand)', 'IS 383:2016', 'cum'),
    MaterialSpec('Coarse aggregate 20 mm', 'IS 383:2016', 'cum'),
    MaterialSpec('Coarse aggregate 10 mm', 'IS 383:2016', 'cum'),
    MaterialSpec('Clay bricks (Class 7.5)', 'IS 1077:1992', 'nos'),
    MaterialSpec('Fly ash bricks', 'IS 12894:2002', 'nos'),
    MaterialSpec('AAC blocks', 'IS 2185 (Part 3)', 'nos'),
    MaterialSpec('Solid concrete blocks', 'IS 2185 (Part 1)', 'nos'),
    MaterialSpec('Admixture (Superplasticiser)', 'IS 9103:1999', 'ltr'),
    MaterialSpec('Fly ash', 'IS 3812 (Part 1)', 'MT'),
    MaterialSpec('Waterproofing compound', 'IS 2645:2003', 'kg'),
    MaterialSpec('Shuttering plywood', 'IS 4990:2011', 'nos'),
    MaterialSpec('Structural steel', 'IS 2062:2011', 'MT'),
    MaterialSpec('Vitrified tiles', 'IS 15622:2017', 'sqm'),
    MaterialSpec('GI / CPVC pipes', 'IS 1239 / IS 15778', 'rmt'),
    MaterialSpec('Electrical conduit (PVC)', 'IS 9537', 'rmt'),
    MaterialSpec('Water for construction', 'IS 456 Cl. 5.4', 'ltr'),
    MaterialSpec('Diesel (HSD)', '-', 'ltr'),
  ];

  static const activities = [
    ActivitySpec('Site clearance & setting out', 'sqm', 'IS 1200 (Pt 1)'),
    ActivitySpec('Earthwork in excavation', 'cum', 'IS 1200 (Pt 1)'),
    ActivitySpec('Back filling & compaction', 'cum', 'IS 1200 (Pt 1)'),
    ActivitySpec('Anti-termite treatment', 'sqm', 'IS 6313 (Pt 2)'),
    ActivitySpec('PCC M10 / M15', 'cum', 'IS 1200 (Pt 2)'),
    ActivitySpec('RCC footing / raft', 'cum', 'IS 456 / IS 1200 (Pt 2)'),
    ActivitySpec('RCC column', 'cum', 'IS 456 / IS 1200 (Pt 2)'),
    ActivitySpec('RCC beam', 'cum', 'IS 456 / IS 1200 (Pt 2)'),
    ActivitySpec('RCC slab', 'cum', 'IS 456 / IS 1200 (Pt 2)'),
    ActivitySpec('RCC retaining / shear wall', 'cum', 'IS 456 / IS 1200 (Pt 2)'),
    ActivitySpec('Formwork / Shuttering', 'sqm', 'IS 14687 / IS 1200 (Pt 5)'),
    ActivitySpec('Reinforcement cutting, bending & fixing', 'MT', 'IS 2502 / IS 1200 (Pt 8)'),
    ActivitySpec('Brick masonry', 'cum', 'IS 2212 / IS 1200 (Pt 3)'),
    ActivitySpec('Block masonry (AAC)', 'cum', 'IS 6041 / IS 1200 (Pt 3)'),
    ActivitySpec('Internal plaster 12 mm', 'sqm', 'IS 1661 / IS 1200 (Pt 12)'),
    ActivitySpec('External plaster 20 mm', 'sqm', 'IS 1661 / IS 1200 (Pt 12)'),
    ActivitySpec('Waterproofing', 'sqm', 'IS 1200 (Pt 15)'),
    ActivitySpec('Flooring / Tiling', 'sqm', 'IS 1200 (Pt 11)'),
    ActivitySpec('Painting', 'sqm', 'IS 2395 / IS 1200 (Pt 15)'),
    ActivitySpec('Door & window fixing', 'nos', 'IS 1200 (Pt 9)'),
    ActivitySpec('Plumbing & sanitary', 'rmt', 'IS 1200 (Pt 16)'),
    ActivitySpec('Electrical conduiting & wiring', 'rmt', 'IS 732'),
    ActivitySpec('Structural steel erection', 'MT', 'IS 800 / IS 1200 (Pt 8)'),
    ActivitySpec('Road work / WBM / GSB', 'cum', 'MoRTH Sec. 400'),
  ];

  static const concreteGrades = [
    'M10', 'M15', 'M20', 'M25', 'M30', 'M35', 'M40', 'M45', 'M50', 'M60',
  ];

  static const qualityTests = [
    TestSpec('Concrete pour record', 'IS 456:2000'),
    TestSpec('Slump test', 'IS 1199 (Part 2)'),
    TestSpec('Cube casting (7 / 28 day)', 'IS 516 (Part 1)'),
    TestSpec('Cube test result', 'IS 516 (Part 1)'),
    TestSpec('Sieve analysis of aggregate', 'IS 2386 (Part 1)'),
    TestSpec('Silt content of sand', 'IS 2386 (Part 2)'),
    TestSpec('Cement setting time / fineness', 'IS 4031'),
    TestSpec('Steel tensile / bend test', 'IS 1608 / IS 1599'),
    TestSpec('Brick compressive strength', 'IS 3495 (Part 1)'),
    TestSpec('Brick water absorption', 'IS 3495 (Part 2)'),
    TestSpec('Field dry density (core cutter)', 'IS 2720 (Part 29)'),
    TestSpec('Plate load test', 'IS 1888'),
    TestSpec('Pile integrity test', 'IS 14893'),
    TestSpec('Pre-pour checklist', 'IS 456 / Project QAP'),
    TestSpec('Reinforcement inspection', 'IS 2502 / Drawings'),
    TestSpec('Shuttering inspection', 'IS 14687'),
    TestSpec('Level / line / plumb check', 'Project QAP'),
  ];

  static const qualityResults = ['Pending', 'Pass', 'Fail', 'Conditional'];

  static const weather = ['Sunny', 'Cloudy', 'Light rain', 'Heavy rain', 'Windy', 'Fog', 'Hot (Heat wave)'];

  static const shifts = [
    'General (9:00 - 18:00)',
    'Day (8:00 - 20:00)',
    'Night (20:00 - 8:00)',
    'Double shift',
  ];

  static const ppe = [
    'Safety helmet (IS 2925)',
    'Safety shoes (IS 15298)',
    'Full body harness (IS 3521)',
    'Reflective jacket (IS 15809)',
    'Hand gloves',
    'Safety goggles (IS 5983)',
    'Ear protection',
    'Dust mask',
  ];

  static const permits = [
    'Work at height',
    'Hot work',
    'Excavation',
    'Confined space',
    'Electrical isolation (LOTO)',
    'Lifting / crane operation',
    'Scaffolding',
  ];

  static const incidentTypes = [
    'Near miss',
    'Unsafe act',
    'Unsafe condition',
    'First aid',
    'Medical treatment',
    'Lost time injury (LTI)',
    'Dangerous occurrence',
    'Property damage',
    'Fatal',
  ];

  static const toolboxTopics = [
    'Work at height & harness use',
    'Housekeeping',
    'Excavation safety',
    'Electrical safety',
    'Lifting & rigging',
    'Fire prevention',
    'Heat stress',
    'Monsoon precautions',
    'PPE usage',
    'Scaffolding safety',
  ];

  static const hindranceCategories = [
    'Drawing / GFC awaited',
    'Design change',
    'Client decision pending',
    'Material shortage',
    'Labour shortage',
    'Machinery breakdown',
    'Rain / weather',
    'Power cut',
    'Water shortage',
    'Site access / encroachment',
    'Statutory approval pending',
    'Payment / funds',
    'Festival / holiday',
    'Local disturbance',
    'Other',
  ];

  static const responsibility = [
    'Client',
    'Contractor',
    'Consultant / PMC',
    'Sub-contractor',
    'Force majeure',
  ];

  static const mediaCategories = [
    'Progress',
    'Quality',
    'Safety',
    'Hindrance',
    'Material receipt',
    'Machinery',
    'Labour',
    'Before',
    'After',
    'Snag / Defect',
    'Visitor / Inspection',
  ];

  static const suggestedTags = [
    'Foundation',
    'Column',
    'Beam',
    'Slab',
    'Shuttering',
    'Reinforcement',
    'Concreting',
    'Curing',
    'Brickwork',
    'Plaster',
    'Waterproofing',
    'MEP',
    'Finishing',
    'PPE',
    'Housekeeping',
    'Cube casting',
    'Pre-pour check',
    'Delay',
    'Rework',
  ];
}
