# Regenerates src/Quartermaster.Documents/Ocr/tessdata-fast-4.1.0.json from tesseract-ocr/tessdata_fast at tag 4.1.0.
# Run by hand; this is the only place the real network is used. Downloads every root *.traineddata into a temp
# folder, records its size and SHA-256, and deletes the folder again.
#   build/ocr-catalog.ps1 [-Out <json>]
param([string]$Out)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

$tag = '4.1.0'
if (-not $Out) { $Out = Join-Path $PSScriptRoot '..\src\Quartermaster.Documents\Ocr\tessdata-fast-4.1.0.json' }

$names = @{
    afr = 'Afrikaans'; amh = 'Amharic'; ara = 'Arabic'; asm = 'Assamese'; aze = 'Azerbaijani'
    aze_cyrl = 'Azerbaijani (Cyrillic)'; bel = 'Belarusian'; ben = 'Bengali'; bod = 'Tibetan'
    bos = 'Bosnian'; bre = 'Breton'; bul = 'Bulgarian'; cat = 'Catalan'; ceb = 'Cebuano'
    ces = 'Czech'; chi_sim = 'Chinese (Simplified)'; chi_sim_vert = 'Chinese (Simplified, vertical)'
    chi_tra = 'Chinese (Traditional)'; chi_tra_vert = 'Chinese (Traditional, vertical)'
    chr = 'Cherokee'; cos = 'Corsican'; cym = 'Welsh'; dan = 'Danish'; deu = 'German'
    div = 'Dhivehi'; dzo = 'Dzongkha'; ell = 'Greek'; eng = 'English'; enm = 'English (Middle, historical)'
    epo = 'Esperanto'; equ = 'Math / equations'; est = 'Estonian'; eus = 'Basque'; fao = 'Faroese'
    fas = 'Persian'; fil = 'Filipino'; fin = 'Finnish'; fra = 'French'; frk = 'German (Fraktur)'
    frm = 'French (Middle, historical)'; fry = 'Western Frisian'; gla = 'Scottish Gaelic'
    gle = 'Irish'; glg = 'Galician'; grc = 'Greek (Ancient)'; guj = 'Gujarati'; hat = 'Haitian Creole'
    heb = 'Hebrew'; hin = 'Hindi'; hrv = 'Croatian'; hun = 'Hungarian'; hye = 'Armenian'
    iku = 'Inuktitut'; ind = 'Indonesian'; isl = 'Icelandic'; ita = 'Italian'
    ita_old = 'Italian (Old, historical)'; jav = 'Javanese'; jpn = 'Japanese'
    jpn_vert = 'Japanese (vertical)'; kan = 'Kannada'; kat = 'Georgian'
    kat_old = 'Georgian (Old, historical)'; kaz = 'Kazakh'; khm = 'Khmer'; kir = 'Kyrgyz'
    kmr = 'Kurdish (Kurmanji)'; kor = 'Korean'; kor_vert = 'Korean (vertical)'; lao = 'Lao'
    lat = 'Latin'; lav = 'Latvian'; lit = 'Lithuanian'; ltz = 'Luxembourgish'; mal = 'Malayalam'
    mar = 'Marathi'; mkd = 'Macedonian'; mlt = 'Maltese'; mon = 'Mongolian'; mri = 'Maori'
    msa = 'Malay'; mya = 'Burmese'; nep = 'Nepali'; nld = 'Dutch'; nor = 'Norwegian'
    oci = 'Occitan'; ori = 'Odia'; osd = 'Orientation and script detection'; pan = 'Punjabi'
    pol = 'Polish'; por = 'Portuguese'; pus = 'Pashto'; que = 'Quechua'; ron = 'Romanian'
    rus = 'Russian'; san = 'Sanskrit'; sin = 'Sinhala'; slk = 'Slovak'; slv = 'Slovenian'
    snd = 'Sindhi'; spa = 'Spanish'; spa_old = 'Spanish (Old, historical)'; sqi = 'Albanian'
    srp = 'Serbian'; srp_latn = 'Serbian (Latin)'; sun = 'Sundanese'; swa = 'Swahili'
    swe = 'Swedish'; syr = 'Syriac'; tam = 'Tamil'; tat = 'Tatar'; tel = 'Telugu'; tgk = 'Tajik'
    tha = 'Thai'; tir = 'Tigrinya'; ton = 'Tongan'; tur = 'Turkish'; uig = 'Uyghur'
    ukr = 'Ukrainian'; urd = 'Urdu'; uzb = 'Uzbek'; uzb_cyrl = 'Uzbek (Cyrillic)'
    vie = 'Vietnamese'; yid = 'Yiddish'; yor = 'Yoruba'
}

$headers = @{ 'User-Agent' = 'Quartermaster-catalog' }
$tree = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/tesseract-ocr/tessdata_fast/git/trees/$tag"
$files = @($tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -like '*.traineddata' -and $_.path -notlike '*/*' })
if ($files.Count -eq 0) { throw 'No traineddata files found.' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('qm-ocr-catalog-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $rows = foreach ($f in ($files | Sort-Object { $_.path -replace '\.traineddata$', '' })) {
        $code = $f.path -replace '\.traineddata$', ''
        $dest = Join-Path $tmp $f.path
        Invoke-WebRequest -Headers $headers -Uri "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/$tag/$($f.path)" -OutFile $dest
        $name = if ($names.ContainsKey($code)) { $names[$code] } else { $code }
        [pscustomobject]@{
            code   = $code
            name   = $name
            bytes  = (Get-Item $dest).Length
            sha256 = (Get-FileHash -Algorithm SHA256 $dest).Hash.ToLowerInvariant()
        }
        Remove-Item $dest
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$rows = @($rows | Sort-Object code -CaseSensitive)
$lines = foreach ($r in $rows) {
    '    { "code": ' + (ConvertTo-Json $r.code) + ', "name": ' + (ConvertTo-Json $r.name) + ', "bytes": ' + $r.bytes + ', "sha256": ' + (ConvertTo-Json $r.sha256) + ' }'
}
$json = "{`n  `"tag`": `"$tag`",`n  `"languages`": [`n" + ($lines -join ",`n") + "`n  ]`n}`n"
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path (Get-Location).Path $Out }
$outPath = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null
[IO.File]::WriteAllText($outPath, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote $($rows.Count) languages to $outPath"
