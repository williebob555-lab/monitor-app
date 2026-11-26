import 'package:material_ui/material_ui.dart';

import 'src/home.dart';
import 'src/host.dart';
import 'src/state.dart';
import 'src/theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final host = await LocalHost.load();
  final state = AppState(host)..start();
  runApp(MonitorApp(state));
}

class MonitorApp extends StatelessWidget {
  const MonitorApp(this.state, {super.key});
  final AppState state;

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'Monitor Lite',
        debugShowCheckedModeBanner: false,
        theme: buildTheme(),
        home: HomePage(state),
      );
}
