fn main() {
    #[cfg(target_os = "windows")]
    {
        let mut resource = winres::WindowsResource::new();
        resource.set_icon("AMARE.ico");
        resource.set("ProductName", "AMARE");
        resource.set("FileDescription", "AMARE Browser");
        resource.set("CompanyName", "AMARE");
        resource.compile().expect("compile AMARE Windows resources");
    }
}
