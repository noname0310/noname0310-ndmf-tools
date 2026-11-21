Shader "Hidden/Noname/ALTO/MeshInputsFallback"
{
    SubShader
    {
        UsePass "Hidden/Noname/ALTO/MeshInputsInactive/MAIN"
    }
    Fallback "Hidden/Noname/Mesh Index Consumer"
}
