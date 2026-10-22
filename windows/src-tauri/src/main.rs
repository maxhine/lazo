// Lazo runs without a console window: Nudo is the whole UI.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    lazo_lib::run()
}
