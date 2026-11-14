# dmgbuild settings for the Quartermaster drag-to-Applications dmg.
# Run via make-dmg.sh: dmgbuild -s dmg-settings.py -D app=<Quartermaster.app> -D background=<png> "<volume>" <out.dmg>
# `defines` is injected by dmgbuild.
app = defines['app']  # noqa: F821

files = [app]
symlinks = {'Applications': '/Applications'}
badge_icon = None
icon_locations = {
    'Quartermaster.app': (160, 200),
    'Applications': (480, 200),
}
background = defines['background']  # noqa: F821
window_rect = ((200, 120), (640, 400))
icon_size = 128
format = 'UDZO'
default_view = 'icon-view'
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
