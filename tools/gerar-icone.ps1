# Gera o ícone do DinoTranslator (.ico) a partir da marca: dois balões de conversa, o dourado
# com o "A" atrás e o branco com o "Я" na frente, sobre um quadrado arredondado azul.
# Desenhado em vetor pelo WPF e salvo em 16 a 256 px (PNG dentro do .ico).
#
# Uso (na raiz do repositório):  powershell -File tools/gerar-icone.ps1

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$saida = Join-Path $root "src\DinoTranslator.Desktop\Assets\DinoTranslator.ico"

function Marca([bool]$letras) {
    $texto = if ($letras) {
        @"
  <Grid Canvas.Left="15.5" Canvas.Top="11.2" Width="20" Height="14">
    <TextBlock Text="A" FontFamily="Segoe UI" FontWeight="Black" FontSize="12.5" Foreground="#3A2600" HorizontalAlignment="Center" VerticalAlignment="Center" />
  </Grid>
  <Grid Canvas.Left="28" Canvas.Top="32" Width="20" Height="14">
    <TextBlock Text="Я" FontFamily="Segoe UI" FontWeight="Black" FontSize="12.5" Foreground="#0A6FE0" HorizontalAlignment="Center" VerticalAlignment="Center" />
  </Grid>
"@
    } else { "" }
    $xaml = @"
<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="64" Height="64">
  <Border Width="64" Height="64" CornerRadius="15">
    <Border.Background>
      <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#3AA5FF" Offset="0" />
        <GradientStop Color="#0A6FE0" Offset="0.55" />
        <GradientStop Color="#4B49D1" Offset="1" />
      </LinearGradientBrush>
    </Border.Background>
  </Border>
  <Path Data="M18 9H36A10 10 0 0 1 46 19V27A10 10 0 0 1 36 37H21.5L12 44L13.5 35.9A10 10 0 0 1 8 27V19A10 10 0 0 1 18 9Z">
    <Path.Fill>
      <LinearGradientBrush MappingMode="Absolute" StartPoint="8,9" EndPoint="46,45">
        <GradientStop Color="#FFE15A" Offset="0" />
        <GradientStop Color="#FF9F0A" Offset="1" />
      </LinearGradientBrush>
    </Path.Fill>
  </Path>
  <Path Data="M30 25H46A10 10 0 0 1 56 35V43A10 10 0 0 1 50.5 51.9L52 60L42.5 53H30A10 10 0 0 1 20 43V35A10 10 0 0 1 30 25Z"
        Stroke="#1F6FE6" StrokeThickness="3.5" StrokeLineJoin="Round">
    <Path.Fill>
      <LinearGradientBrush MappingMode="Absolute" StartPoint="20,25" EndPoint="56,61">
        <GradientStop Color="#FFFFFF" Offset="0" />
        <GradientStop Color="#DCE6FF" Offset="1" />
      </LinearGradientBrush>
    </Path.Fill>
  </Path>
$texto
</Canvas>
"@
    return [Windows.Markup.XamlReader]::Parse($xaml)
}

$tamanhos = 16, 24, 32, 48, 64, 128, 256
$imagens = foreach ($tamanho in $tamanhos) {
    $caixa = New-Object Windows.Controls.Viewbox
    $caixa.Width = $tamanho
    $caixa.Height = $tamanho
    # Abaixo de 32 px as letras viram ruído: fica só o encontro dos balões.
    $caixa.Child = Marca ($tamanho -ge 32)
    $caixa.Measure((New-Object Windows.Size $tamanho, $tamanho))
    $caixa.Arrange((New-Object Windows.Rect 0, 0, $tamanho, $tamanho))
    $caixa.UpdateLayout()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $tamanho, $tamanho, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($caixa)
    $codificador = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $codificador.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memoria = New-Object IO.MemoryStream
    $codificador.Save($memoria)
    , $memoria.ToArray()
}

# Formato .ico: cabeçalho, uma entrada de 16 bytes por tamanho e as imagens PNG em seguida.
New-Item -ItemType Directory -Force (Split-Path $saida) | Out-Null
$arquivo = New-Object IO.MemoryStream
$escritor = New-Object IO.BinaryWriter $arquivo
$escritor.Write([uint16]0)
$escritor.Write([uint16]1)
$escritor.Write([uint16]$tamanhos.Count)
$posicao = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
    $lado = if ($tamanhos[$i] -ge 256) { 0 } else { $tamanhos[$i] }
    $escritor.Write([byte]$lado)
    $escritor.Write([byte]$lado)
    $escritor.Write([byte]0)
    $escritor.Write([byte]0)
    $escritor.Write([uint16]1)
    $escritor.Write([uint16]32)
    $escritor.Write([uint32]$imagens[$i].Length)
    $escritor.Write([uint32]$posicao)
    $posicao += $imagens[$i].Length
}
foreach ($imagem in $imagens) { $escritor.Write($imagem) }
$escritor.Flush()
[IO.File]::WriteAllBytes($saida, $arquivo.ToArray())
"Ícone: $saida ($([math]::Round($arquivo.Length / 1KB)) KB)"

# Prévia em PNG de 256 px ao lado do ícone, para conferir o desenho.
[IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($saida, ".png"), $imagens[-1])
