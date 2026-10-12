window.sqlTools = {
    copyText: async function (text) {
        await navigator.clipboard.writeText(text);
    },
    downloadText: function (fileName, text) {
        const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
    /**
     * Lê a imagem escolhida em um <input type="file">, corta em quadrado (centro), reduz para size x size e converte para
     * JPEG, com qualidade decrescente até caber em maxBytes. Só o resultado pequeno vai para o servidor (em base64).
     * Retorna { data } ou { error }. SVG é recusado (pode conter script).
     */
    resizeImageFromInput: async function (input, size, maxBytes) {
        const file = input && input.files && input.files[0];
        if (!file) return { error: "Nenhuma imagem selecionada." };
        if (!file.type.startsWith("image/") || file.type === "image/svg+xml")
            return { error: "Escolha uma imagem JPG, PNG, WebP ou GIF." };
        if (file.size > 15 * 1024 * 1024)
            return { error: "A imagem é grande demais (máximo de 15 MB)." };

        let bitmap;
        try {
            bitmap = await createImageBitmap(file);
        } catch {
            return { error: "Não foi possível ler essa imagem." };
        }

        const side = Math.min(bitmap.width, bitmap.height);
        const canvas = document.createElement("canvas");
        canvas.width = canvas.height = size;
        const context = canvas.getContext("2d");
        context.fillStyle = "#ffffff"; // PNG/WebP transparentes viram fundo branco no JPEG
        context.fillRect(0, 0, size, size);
        context.imageSmoothingQuality = "high";
        context.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, size, size);
        if (bitmap.close) bitmap.close();

        let quality = 0.9;
        let dataUrl;
        do {
            dataUrl = canvas.toDataURL("image/jpeg", quality);
            quality -= 0.1;
        } while (dataUrl.length * 0.75 > maxBytes && quality > 0.3);

        input.value = ""; // permite escolher o mesmo arquivo de novo
        return { data: dataUrl.split(",")[1] };
    },
    // Preferências simples da interface (ex.: grupos do menu abertos). Falhas (modo privado etc.) são ignoradas.
    getItem: function (key) {
        try { return window.localStorage.getItem(key); } catch { return null; }
    },
    setItem: function (key, value) {
        try { window.localStorage.setItem(key, value); } catch { }
    }
};
