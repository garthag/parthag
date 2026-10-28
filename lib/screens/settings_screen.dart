import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../services/cloud_sync.dart';
import '../state/app_state.dart';
import '../widgets/common.dart';

class SettingsScreen extends StatefulWidget {
  const SettingsScreen({super.key});

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  late final _profile = context.read<AppState>().profile;
  late final _name = TextEditingController(text: _profile.name);
  late final _designation = TextEditingController(text: _profile.designation);
  late final _company = TextEditingController(text: _profile.company);

  @override
  void dispose() {
    _name.dispose();
    _designation.dispose();
    _company.dispose();
    super.dispose();
  }

  Future<void> _saveProfile() async {
    await context.read<AppState>().saveProfile(UserProfile(
          name: _name.text.trim(),
          designation: _designation.text.trim(),
          company: _company.text.trim(),
        ));
    if (mounted) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Profile saved')));
    }
  }

  @override
  Widget build(BuildContext context) {
    final cloud = context.watch<CloudSync>();
    final state = context.read<AppState>();
    final t = Theme.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Settings')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text('Your profile', style: t.textTheme.titleMedium),
          const SizedBox(height: 4),
          Text('Used as "Prepared by" on new DPRs.', style: t.textTheme.bodySmall),
          const SizedBox(height: 12),
          TextField(controller: _name, decoration: const InputDecoration(labelText: 'Name')),
          const SizedBox(height: 12),
          TextField(controller: _designation, decoration: const InputDecoration(labelText: 'Designation')),
          const SizedBox(height: 12),
          TextField(controller: _company, decoration: const InputDecoration(labelText: 'Company')),
          const SizedBox(height: 12),
          FilledButton(onPressed: _saveProfile, child: const Text('Save profile')),
          const Divider(height: 40),
          Text('Google cloud backup & sync', style: t.textTheme.titleMedium),
          const SizedBox(height: 8),
          if (!cloud.available)
            const Card(
              child: ListTile(
                leading: Icon(Icons.cloud_off_outlined),
                title: Text('Offline mode'),
                subtitle: Text(
                    'All data is stored on this phone. To enable Google sign-in and cloud sync, '
                    'connect the app to Firebase (see README: "Enable Google cloud sync").'),
              ),
            )
          else if (cloud.user == null)
            FilledButton.icon(
              onPressed: cloud.signInWithGoogle,
              icon: const Icon(Icons.login),
              label: const Text('Sign in with Google'),
            )
          else ...[
            Card(
              child: ListTile(
                leading: const Icon(Icons.account_circle_outlined),
                title: Text(cloud.user!.displayName ?? cloud.user!.email ?? 'Signed in'),
                subtitle: Text(cloud.lastSync == null
                    ? 'Not synced yet'
                    : 'Last sync: ${timeFmt.format(cloud.lastSync!)}'),
                trailing: TextButton(onPressed: cloud.signOut, child: const Text('Sign out')),
              ),
            ),
            const SizedBox(height: 8),
            FilledButton.icon(
              onPressed: cloud.syncing ? null : () => cloud.sync(state),
              icon: const Icon(Icons.cloud_sync_outlined),
              label: Text(cloud.syncing ? cloud.progress : 'Sync now'),
            ),
          ],
          if (cloud.error != null) ...[
            const SizedBox(height: 8),
            Text(cloud.error!, style: TextStyle(color: t.colorScheme.error)),
          ],
          const Divider(height: 40),
          Text('Standards followed', style: t.textTheme.titleMedium),
          const SizedBox(height: 8),
          const Text(
            '• IS 1200 – Method of measurement & units\n'
            '• IS 456:2000 – Concrete grades, pour & curing records\n'
            '• IS 516 / IS 1199 – Cube & slump tests\n'
            '• IS 383, IS 269, IS 1489, IS 1786 – Material specifications\n'
            '• IS 7969, IS 3764 & BOCW Act 1996 – Safety records\n'
            '• IS 2925, IS 3521, IS 15298 – PPE\n'
            '• Minimum Wages Act skill classification for manpower\n'
            '• CPWD-style hindrance register for EOT claims\n'
            '• Dates in DD-MM-YYYY, numbers in Indian grouping (lakh / crore)',
          ),
        ],
      ),
    );
  }
}
